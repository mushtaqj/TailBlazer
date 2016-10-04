using System;
using System.IO;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text;
using DynamicData;
using DynamicData.Kernel;
using TailBlazer.Domain.Annotations;
using TailBlazer.Domain.Infrastructure;

namespace TailBlazer.Domain.FileHandling
{
    /// <summary>
    /// Responsive and flexible file searching.
    /// See https://github.com/RolandPheasant/TailBlazer/issues/42
    /// </summary>
    public class FileSearchIndexer 
    {
        private readonly IScheduler _scheduler;
        private readonly IObservable<FileSegmentsWithTail> _fileSegments;
        private readonly Func<string, bool> _predicate;
        private readonly int _arbitaryNumberOfMatchesBeforeWeBailOutBecauseMemoryGetsHammered;
        
        public IObservable<FileSearchCollection> SearchResult { get; }

        public FileSearchIndexer([NotNull] IObservable<FileSegmentsWithTail> fileSegments,
            [NotNull] Func<string, bool> predicate,
            int arbitaryNumberOfMatchesBeforeWeBailOutBecauseMemoryGetsHammered = 50000,
            IScheduler scheduler = null)
        {
            if (fileSegments == null) throw new ArgumentNullException(nameof(fileSegments));
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));

            _fileSegments = fileSegments;
            _predicate = predicate;
            _arbitaryNumberOfMatchesBeforeWeBailOutBecauseMemoryGetsHammered = arbitaryNumberOfMatchesBeforeWeBailOutBecauseMemoryGetsHammered;
            _scheduler = scheduler ?? Scheduler.Default;

            SearchResult = BuildObservable();
        }

        private IObservable<FileSearchCollection> BuildObservable()
        {

            return Observable.Create<FileSearchCollection>(observer =>
            {
                var shared = _fileSegments.Publish();

                //0.5 ensure we have the latest file info and encoding
                Encoding encoding = null;
                FileInfo fileInfo = null;

                //TODO: This is shit. We need a better way of passing around the Encoding / File meta data
                var infoSubscriber = shared
                    .Where(fs => fs.Segments.Encoding != null)
                    .Take(1)
                    .Subscribe(fswt =>
                    {
                        encoding = fswt.Segments.Encoding;
                        fileInfo = fswt.Segments.Info;
                    });

                //Create a cache of segments which are to be searched
                var segmentCache = shared.Select(s => s.Segments.Segments)
                    .ToObservableChangeSet(s => s.Key)
                    .IgnoreUpdateWhen((current, previous) => current == previous)
                    .AsObservableCache();

                var tailWatcher = shared
                    .Select(segments => segments.TailInfo)
                    .DistinctUntilChanged();

                //manually maintained search results and status
                var searchData = new SourceCache<FileSegmentSearch, FileSegmentKey>(s => s.Key);

                var publisher = searchData.Connect()
                    .Flatten()
                    .Select(change => change.Current)
                    .CombineLatest(tailWatcher, (segment, tail) => new { Segment = segment, Tail = tail })
                    .Scan((FileSearchCollection)null, (previous, current) => previous == null
                        ? new FileSearchCollection(current.Segment, current.Tail, fileInfo, encoding, _arbitaryNumberOfMatchesBeforeWeBailOutBecauseMemoryGetsHammered)
                        : new FileSearchCollection(previous, current.Segment, current.Tail, fileInfo, encoding, _arbitaryNumberOfMatchesBeforeWeBailOutBecauseMemoryGetsHammered))
                    .StartWith(FileSearchCollection.None)
                    .SubscribeSafe(observer);   

                //initialise a pending state for all segments
                var cacheLoader = segmentCache.Connect()
                    .Transform(fs => new FileSegmentSearch(fs))
                    .WhereReasonsAre(ChangeReason.Add)
                    .PopulateInto(searchData);

                //continual indexing of the tail + replace tail index whenether there are new scan results
                var tailScanner = tailWatcher
                    .Scan((FileSegmentSearch)null, (previous, tail) =>
                    {
                        var tailSegment = searchData.Lookup(FileSegmentKey.Tail)
                            .ValueOrThrow(()=>new MissingKeyException("Tail is missing"));

                        
                        if (previous == null)
                        {
                            var result = Search(fileInfo.FullName, encoding, tailSegment.Segment.Start, tailSegment.Segment.End);
                            return new FileSegmentSearch(tailSegment, result);
                        }
                        else
                        {
                            var lines = tail.Lines.Where(line => _predicate(line.Text)).ToArray();
                            var indicies = lines.Select(l => l.LineInfo.Start).ToArray();
                            var result = new FileSegmentSearchResult(tail.Start, tail.End, indicies);
                            return new FileSegmentSearch(tailSegment, result);
                        }
                    })
                    .DistinctUntilChanged()
                    .Subscribe(tail => searchData.AddOrUpdate(tail));


                //load the rest of the file segment by segment, reporting status after each search
                var headSubscriber = shared.Take(1).WithContinuation(() =>
                {
                    var locker = new object();

                    return searchData.Connect(fss => fss.Segment.Type == FileSegmentType.Head)
                        .WhereReasonsAre(ChangeReason.Add)
                        .SelectMany(changes => changes.Select(c => c.Current).OrderByDescending(c => c.Segment.Index).ToArray())
                        .ObserveOn(_scheduler)
                        .Synchronize(locker)
                        .Do(head => searchData.AddOrUpdate(new FileSegmentSearch(head.Segment, FileSegmentSearchStatus.Searching)))
                        .Select(fileSegmentSearch =>
                        {
                                /*
                                    This hack imposes a limitation on the number of items returned as memory can be 
                                    absolutely hammered [I have seen 20MB memory when searching a 1 GB file - obviously not an option]
                                   TODO: A proper solution. 
                                           1. How about index to file?
                                           2. Allow auto pipe of large files
                                           3. Allow user to have some control here
                               */

                            var sum = searchData.Items.Sum(fss => fss.Lines.Length);

                            if (sum >= _arbitaryNumberOfMatchesBeforeWeBailOutBecauseMemoryGetsHammered)
                            {
                                return new FileSegmentSearch(fileSegmentSearch, FileSegmentSearchStatus.Complete);
                            }
                            var result = Search(fileInfo.FullName, encoding, fileSegmentSearch.Segment.Start, fileSegmentSearch.Segment.End);
                            return new FileSegmentSearch(fileSegmentSearch, result);
                        });
                })
                    .Subscribe(head => searchData.AddOrUpdate(head));

                return new CompositeDisposable(
                    segmentCache,
                    publisher,
                    cacheLoader,
                    tailScanner,
                    headSubscriber,
                    shared.Connect(),
                    infoSubscriber,
                    searchData);
            });
        }


        //private class FileSegmentSearchWithTail
        //{
        //    public FileSegmentSearch FileSegmentSearch { get; }
        //    public FileTailInfo FileTail { get; }

        //    public FileSegmentSearchWithTail(FileSegmentSearch fileSegmentSearch, FileTailInfo fileTail)
        //    {
        //        FileSegmentSearch = fileSegmentSearch;
        //        FileTail = fileTail;
        //    }

        //}

        private FileSegmentSearchResult Search(string fileName, Encoding encoding, long start, long end)
        {
            long lastPosition = 0;
            using (var stream = File.Open(fileName, FileMode.Open, FileAccess.Read, FileShare.Delete | FileShare.ReadWrite))
            {
                if (stream.Length < start)
                {
                    start = 0;
                    end = stream.Length;
                }

                long[] lines;
                using (var reader = new StreamReaderExtended(stream, encoding, false))
                {
                    stream.Seek(start, SeekOrigin.Begin);
                    if (reader.EndOfStream)
                        return new FileSegmentSearchResult(start, end);

                    lines = reader.SearchLines(_predicate, i => i, (line, position) =>
                    {
                        lastPosition = position;//this is end of line
                        return end != -1 && lastPosition > end;

                    }).ToArray();
                }
                return new FileSegmentSearchResult(start, lastPosition, lines);
            }
        }
    }
}