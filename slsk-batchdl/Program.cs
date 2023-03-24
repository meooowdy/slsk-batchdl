﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel.Design.Serialization;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Soulseek;
using TagLib.Matroska;
using static System.Formats.Asn1.AsnWriter;
using static System.Net.WebRequestMethods;

class Program
{
    static SoulseekClient client = new SoulseekClient();
    static Dictionary<Track, SearchInfo> searches = new Dictionary<Track, SearchInfo>();
    static Dictionary<string, DownloadInfo> downloads = new Dictionary<string, DownloadInfo>();
    static List<Track> tracks = new List<Track>();
    static string outputFolder = "";
    static string failsFilePath = "";
    static string m3uFilePath = "";
    static string musicDir = "";
    static int downloadMaxStaleTime = 0;
#if DEBUG
    static int displayUpdateDelay = 1000;
#else
    static int displayUpdateDelay = 500;
#endif

    static void PrintHelp()
    {
        Console.WriteLine("Usage: slsk-batchdl.exe [OPTIONS]");
        Console.WriteLine("Options:");
        Console.WriteLine("  --output-folder <path>        Downloaded files will be placed here");
        Console.WriteLine("  --tracks-csv <path>          The csv file containing track information (in case it's not in the output folder)");
        Console.WriteLine("  --username <username>        Soulseek username");
        Console.WriteLine("  --password <password>        Soulseek password");
        Console.WriteLine("  --artist-col <column>        Specify if the csv file contains an artist name column");
        Console.WriteLine("  --track-col <column>         Specify if if the csv file contains an track name column");
        Console.WriteLine("  --full-title-col <column>    Specify only if there are no separate artist and track name columns are not in the csv");
        Console.WriteLine("  --uploader-col <column>      Specify when using full title col if there is also an uploader col in the csv (fallback in case artist name cannot be extracted from title)");
        Console.WriteLine("  --length-col <column>        Specify the name of the track duration column, if exists");
        Console.WriteLine("  --time-unit <unit>           Time unit for the track duration column, ms or s");
        Console.WriteLine("  --skip-existing              Skip if a track matching the conditions is found in the output folder or your music library (if provided)");
        //Console.WriteLine("  --music-dir <path>           Specify to also skip downloading tracks which are in your library, use with --skip-existing (currently too slow / unusable)");
        Console.WriteLine("  --skip-if-pref-failed        Skip if preferred versions of a track exist but failed to download. If no pref. versions were found, download as normal.");
        Console.WriteLine("  --create-m3u                 Create an m3u playlist file in the output dir.");
        Console.WriteLine("  --search-timeout <timeout>   Maximal search time");
        Console.WriteLine("  --download-max-stale-time <time> Maximal download time with no progress");
        Console.WriteLine("  --max-concurrent-processes <num> Max concurrent searches / downloads (unstable)");
        Console.WriteLine("  --max-retries-per-file <num> Maximum number of users to try downloading from before skipping track");
        Console.WriteLine("  --pref-format <format>       Preferred file format; files satisfying these will be downloaded first");
        Console.WriteLine("  --pref-length-tolerance <tol> Preferred length tolerance (if length col provided); files satisfying these will be downloaded first");
        Console.WriteLine("  --pref-min-bitrate <rate>    Preferred minimum bitrate; files satisfying these will be downloaded first");
        Console.WriteLine("  --pref-max-bitrate <rate>    Preferred maximum bitrate; files satisfying these will be downloaded first");
        Console.WriteLine("  --pref-max-sample-rate <rate> Preferred maximum sample rate; files satisfying these will be downloaded first");
        Console.WriteLine("  --nec-format <format>        Necessary file format; files not satisfying this are not downloaded");
        Console.WriteLine("  --nec-length-tolerance <tol> Necessary length tolerance (if length col provided); files not satisfying this are not downloaded");
        Console.WriteLine("  --nec-min-bitrate <rate>     Necessary minimum bitrate; files not satisfying this are not downloaded");
        Console.WriteLine("  --nec-max-bitrate <rate>     Necessary maximum bitrate; files not satisfying this are not downloaded");
        Console.WriteLine("  --nec-max-sample-rate <rate> Necessary maximum sample rate; files not satisfying this are not downloaded");
    }

    static async Task Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine();
        lastLine = Console.CursorTop;
        if (args.Contains("--help"))
        {
            PrintHelp();
            return;
        }

        //tracks = new List<Track>()
        //{
        //    new Track { ArtistName = "Daft Punk", TrackTitle = "One More Time", Length = 320 },
        //    new Track { ArtistName = "The Chemical Brothers", TrackTitle = "Block Rockin' Beats", Length = 294 },
        //    new Track { ArtistName = "Fatboy Slim", TrackTitle = "Praise You", Length = 324 },
        //    new Track { ArtistName = "The Prodigy", TrackTitle = "Firestarter", Length = 279 },
        //    new Track { ArtistName = "Underworld", TrackTitle = "Born Slippy", Length = 456 },
        //    new Track { ArtistName = "Orbital", TrackTitle = "Chime", Length = 194 },
        //    new Track { ArtistName = "Aphex Twin", TrackTitle = "Windowlicker", Length = 365 },
        //    new Track { ArtistName = "Moby", TrackTitle = "Porcelain", Length = 238 },
        //    new Track { ArtistName = "Leftfield", TrackTitle = "Phat Planet", Length = 323 },
        //    new Track { ArtistName = "The Crystal Method", TrackTitle = "Busy Child", Length = 444 },
        //    new Track { ArtistName = "Real Musician", TrackTitle = "This track does not EXIST!!! LOL!!!! LMAO!!!! HAHAHAHHAA!!! !", Length = 324 }
        //};
        outputFolder = "";
        musicDir = "";
        string tracksCsv = "";
        string username = "";
        string password = "";
        string artistCol = "";
        string trackCol = "";
        string fullTitleCol = "";
        string uploaderCol = "";
        string lengthCol = "";
        string timeUnit = "s";
        bool skipExisting = false;
        bool skipIfPrefFailed = false;
        bool createM3u = false;
        int searchTimeout = 15000;
        downloadMaxStaleTime = 60000;
        int maxConcurrentProcesses = 2;
        int maxRetriesPerFile = 30;
        var preferredCond = new FileConditions
        {
            Format = "mp3",
            LengthTolerance = 3,
            MinBitrate = 200,
            MaxBitrate = 2200,
            MaxSampleRate = 96000
        };
        var necessaryCond = new FileConditions
        {
            LengthTolerance = 3,
            Format = "",
            MinBitrate = -1,
            MaxBitrate = -1,
            MaxSampleRate = -1,
        };


        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--output-folder":
                    outputFolder = args[++i];
                    break;
                case "--music-dir":
                    musicDir = args[++i];
                    break;
                case "--tracks-csv":
                    tracksCsv = args[++i];
                    break;
                case "--username":
                    username = args[++i];
                    break;
                case "--password":
                    password = args[++i];
                    break;
                case "--artist-col":
                    artistCol = args[++i];
                    break;
                case "--track-col":
                    trackCol = args[++i];
                    break;
                case "--full-title-col":
                    fullTitleCol = args[++i];
                    break;
                case "--uploader-col":
                    uploaderCol = args[++i];
                    break;
                case "--length-col":
                    lengthCol = args[++i];
                    break;
                case "--time-unit":
                    timeUnit = args[++i];
                    break;
                case "--skip-existing":
                    skipExisting = true;
                    break;
                case "--skip-if-pref-failed":
                    skipIfPrefFailed = true;
                    break;
                case "--create-m3u":
                    createM3u = true;
                    break;
                case "--search-timeout":
                    searchTimeout = int.Parse(args[++i]);
                    break;
                case "--download-max-stale-time":
                    downloadMaxStaleTime = int.Parse(args[++i]);
                    break;
                case "--max-concurrent-processes":
                    maxConcurrentProcesses = int.Parse(args[++i]);
                    break;
                case "--max-retries-per-file":
                    maxRetriesPerFile = int.Parse(args[++i]);
                    break;
                case "--pref-format":
                    preferredCond.Format = args[++i];
                    break;
                case "--pref-length-tolerance":
                    preferredCond.LengthTolerance = int.Parse(args[++i]);
                    break;
                case "--pref-min-bitrate":
                    preferredCond.MinBitrate = int.Parse(args[++i]);
                    break;
                case "--pref-max-bitrate":
                    preferredCond.MaxBitrate = int.Parse(args[++i]);
                    break;
                case "--pref-max-sample-rate":
                    preferredCond.MaxSampleRate = int.Parse(args[++i]);
                    break;
                case "--nec-format":
                    necessaryCond.Format = args[++i];
                    break;
                case "--nec-length-tolerance":
                    necessaryCond.LengthTolerance = int.Parse(args[++i]);
                    break;
                case "--nec-min-bitrate":
                    necessaryCond.MinBitrate = int.Parse(args[++i]);
                    break;
                case "--nec-max-bitrate":
                    necessaryCond.MaxBitrate = int.Parse(args[++i]);
                    break;
                case "--nec-max-sample-rate":
                    necessaryCond.MaxSampleRate = int.Parse(args[++i]);
                    break;
                default:
                    Console.WriteLine($"Unknown argument: {args[i]}");
                    break;
            }
        }

        AppDomain.CurrentDomain.UnhandledException += (sender, e) => {
            Console.WriteLine($"{e.ExceptionObject}");
        };

        if ((trackCol == "" && artistCol == "" && fullTitleCol == "") || (trackCol != "" && artistCol == "") || (fullTitleCol != "" && (artistCol != "" || trackCol != "")))
            throw new Exception("Use one of: full title column, (artist column AND track name)");
        if (lengthCol == "")
            WriteLastLine($"Warning: No lenght column specified, results may be imprecise.");

        System.IO.Directory.CreateDirectory(outputFolder);

        string[] csvFiles = System.IO.Directory.GetFiles(outputFolder, "*.csv");
        if ((tracksCsv != "" && !System.IO.File.Exists(tracksCsv)) && csvFiles.Length == 0)
            throw new Exception("csv file not found");
        if (tracksCsv == "" && csvFiles.Length > 0)
            tracksCsv = csvFiles[0];

        tracks = ParseCsvIntoTrackInfo(tracksCsv, artistCol, trackCol, lengthCol, fullTitleCol, uploaderCol, timeUnit: timeUnit);
        Track[] tmp = new Track[tracks.Count];
        tracks.CopyTo(tmp);
        var tracksStart = tmp.ToList();

        failsFilePath = Path.Combine(outputFolder, "_failed.txt");
        if (System.IO.File.Exists(failsFilePath))
        {
            WriteAllLinesOutputFile("");
            try { System.IO.File.Delete(failsFilePath); }
            catch { }
        }
        m3uFilePath = Path.Combine(outputFolder, "playlist.m3u");
        List<string> m3uLines = Enumerable.Repeat("", tracksStart.Count).ToList();

        if (skipExisting && tracks.First().Length > 0)
        {
            WriteLastLine("Checking if tracks exist in output folder...");
            var outputDirFiles = System.IO.Directory.GetFiles(outputFolder, "*", SearchOption.AllDirectories);
            var musicFiles = outputDirFiles
                .Where(filename =>
                {
                    try { return IsMusicFile(filename) && TagLib.File.Create(filename) != null; }
                    catch { return false; }
                });
            tracks = tracks.Where(x =>
            {
                bool exists = FileExistsInCollection(x.TrackTitle == "" ? x.UnparsedTitle : x.TrackTitle, x.Length, necessaryCond, musicFiles, out string path);
                if (exists)
                    m3uLines[tracksStart.IndexOf(x)] = path;
                return !exists;
            }).ToList();
        }
        if (musicDir != "" && tracks.First().Length > 0)
        {
            WriteLastLine("Checking if tracks exist in library...");
            var musicDirFiles = System.IO.Directory.GetFiles(musicDir, "*", SearchOption.AllDirectories).Where(x => IsMusicFile(x));
            var musicFiles = musicDirFiles
                .Where(filename =>
                {
                    try { return IsMusicFile(filename) && TagLib.File.Create(filename) != null; }
                    catch { return false; }
                });
            tracks = tracks.Where(x =>
            {
                bool exists = FileExistsInCollection(x.TrackTitle == "" ? x.UnparsedTitle : x.TrackTitle, x.Length, necessaryCond, musicFiles, out string path);
                if (exists)
                    m3uLines[tracksStart.IndexOf(x)] = path;
                return !exists;
            }).ToList();
            WriteLastLine("Done");
        }

        int tracksRemaining = tracks.Count;

        //foreach (var track in tracks)
        //    WriteLastLine($"{track.Title}, {track.ArtistName} - {track.TackTitle} ({track.Length}s)");

        await client.ConnectAsync(username, password);

        var UpdateTask = Task.Run(() => Update());
        SemaphoreSlim semaphore = new SemaphoreSlim(maxConcurrentProcesses);

        string alreadyExist = skipExisting && tracksStart.Count - tracks.Count > 0 ? $" ({tracksStart.Count - tracks.Count} already exist)" : "";
        WriteLastLine($"Downloading {tracks.Count} tracks{alreadyExist}");

        var downloadTasks = tracks.Select(async (track) =>
        {
            await semaphore.WaitAsync();
            try
            {
                var savedFilePath = await SearchAndDownload(track, preferredCond, necessaryCond, skipIfPrefFailed, maxRetriesPerFile, searchTimeout);
                if (savedFilePath != "")
                {
                    tracksRemaining--;
                    m3uLines[tracksStart.IndexOf(track)] = savedFilePath;
                    Debug.WriteLine($"Saved at: {savedFilePath}");
                    if (createM3u)
                    {
                        using (var fileStream = new FileStream(m3uFilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite))
                        using (var streamWriter = new StreamWriter(fileStream, System.Text.Encoding.UTF8))
                        {
                            foreach (var line in m3uLines)
                                streamWriter.WriteLine(line);
                        }
                    }
                }
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(downloadTasks);

        WriteLastLine($"\nDownloaded {tracks.Count - tracksRemaining} of {tracks.Count} tracks");
        if (System.IO.File.Exists(failsFilePath))
            WriteLastLine($"Failed to download:\n{System.IO.File.ReadAllText(failsFilePath)}");
    }

    static async Task<string> SearchAndDownload(Track track, FileConditions preferredCond, FileConditions necessaryCond, bool skipIfPrefFailed, int maxRetriesPerFile, int searchTimeout)
    {
        var title = track.TrackTitle == "" ? $"{track.UnparsedTitle}" : $"{track.ArtistName} - {track.TrackTitle}";
        if (track.TrackTitle == "")
        {
            var t = track.UnparsedTitle.Split('-', StringSplitOptions.TrimEntries);
            if (t.Length == 1 && t[0] != "" && t[1] != "")
                title = $"{t[0]} - {t[1]}";
            else if (track.Uploader != "" && !track.UnparsedTitle.Contains(track.Uploader))
                title = $"{track.Uploader} - {track.UnparsedTitle}";
        }
        var saveFilePath = "";

        WriteLastLine($"Searching for {title}");

        var searchQuery = SearchQuery.FromText($"{title}");
        var searchOptions = new SearchOptions
        (
            minimumPeerUploadSpeed: 1, searchTimeout: searchTimeout,
            responseFilter: (response) =>
            {
                return response.UploadSpeed > 0 && response.HasFreeUploadSlot;
            },
            fileFilter: (file) =>
            {
                return IsMusicFile(file.Filename) && necessaryCond.FileSatisfies(file, track.Length);
            }
        );

        bool attemptedDownloadPref = false;
        Task downloadTask = null;
        bool downloading = false;
        var responses = new List<SearchResponse>();
        var cts = new CancellationTokenSource();

        lock (searches) {
            searches[track] = new SearchInfo(searchQuery, responses, searchOptions);
        }

        try
        {
            var search = await client.SearchAsync(searchQuery, options: searchOptions, cancellationToken: cts.Token, responseHandler: (r) =>
            {
                if (r.Files.Count > 0)
                {
                    responses.Add(r);
                    if (!downloading)
                    {
                        var f = r.Files.First();
                        if (preferredCond.FileSatisfies(f, track.Length) && r.HasFreeUploadSlot && r.UploadSpeed / 1000000 >= 1)
                        {
                            Debug.WriteLine("Early download");
                            downloading = true;
                            saveFilePath = GetSavePath(f, track);
                            attemptedDownloadPref = true;
                            try
                            {
                                downloadTask = DownloadFile(r, f, saveFilePath, cts);
                            }
                            catch
                            {
                                saveFilePath = "";
                                downloading = false;
                            }
                        }
                    }
                }
            });
        }
        catch (Exception e)
        {
            if (responses.Count == 0 && !downloading)
            {
                lock (searches) { searches.Remove(track); }
                WriteLastLine($"Search {title} failed, skipping: {e.Message}", ConsoleColor.Red);
                cts.Dispose();
                return "";
            }
        }

        lock (searches) { searches.Remove(track); }
        Debug.WriteLine($"Found {responses.Count} responses");

        if (downloading)
        {
            try
            {
                await downloadTask;
            }
            catch
            {
                saveFilePath = "";
                downloading = false;
            }
        }

        if (!downloading)
        {
            var fileResponses = responses
                .SelectMany(response => response.Files.Select(file => (response, file)))
                .OrderByDescending(x => preferredCond.LengthToleranceSatisfies(x.file, track.Length))
                .ThenByDescending(x => preferredCond.BitrateSatisfies(x.file))
                .ThenByDescending(x => preferredCond.FileSatisfies(x.file, track.Length))
                .ThenByDescending(x => x.response.HasFreeUploadSlot)
                .ThenByDescending(x => x.response.UploadSpeed)
                .ToList();

            if (fileResponses.Count == 0)
            {
                WriteLastLine($"Failed to find: {title}, skipping", ConsoleColor.Red);
                var failedDownloadInfo = $"{title} ({track.Length}s) [Reason: No file found with matching criteria]";
                WriteLineOutputFile(failedDownloadInfo);
                return "";
            }

            int downloadRetries = maxRetriesPerFile;
            foreach (var x in fileResponses)
            {
                bool pref = preferredCond.FileSatisfies(x.file, track.Length);
                if (skipIfPrefFailed && attemptedDownloadPref && !pref)
                {
                    WriteLastLine($"Pref. version of the file exists, but couldn't be downloaded: {title}, skipping", ConsoleColor.Red);
                    var failedDownloadInfo = $"{title} ({track.Length}s) [Preferred version of the file exists, but couldn't be downloaded]";
                    WriteLineOutputFile(failedDownloadInfo);
                    saveFilePath = "";
                    break;
                }
                else if (pref)
                    attemptedDownloadPref = true;

                saveFilePath = GetSavePath(x.file, track);

                try
                {
                    await DownloadFile(x.response, x.file, saveFilePath);
                    break;
                }
                catch
                {
                    if (--downloadRetries <= 0)
                    {
                        saveFilePath = "";
                        WriteLastLine($"Failed to download: {title}, skipping", ConsoleColor.Red);
                        var failedDownloadInfo = $"{title} ({track.Length}s) [Reason: Out of download retries]";
                        WriteLineOutputFile(failedDownloadInfo);
                        break;
                    }
                }
            }
        }

        cts.Dispose();
        return saveFilePath;
    }

    static async Task Update()
    {
        while (true)
        {
            string debugSearches = $"Searches ({searches.Count}):\n";
            string debugDownloads = $"Downloads ({downloads.Count}):\n";

            foreach (var (key, val) in searches)
            {
                if (val != null)
                    debugSearches += val.query.SearchText + "\n";
                else
                    lock (searches) { searches.Remove(key); }
            }

            foreach (var (key, val) in downloads)
            {
                if (val != null)
                {
                    float? percentage = val.bytesTransferred / (float)val.file.Size;
                    string x = $"({percentage:P}): {val.response.Username}({val.response.HasFreeUploadSlot}/{val.response.QueueLength}) \\ {val.file.Filename.Split('\\').Last()}";
                    if (val.transfer != null)
                        debugDownloads += $"{val.transfer.State} {x}\n";
                    else
                        debugDownloads += $"NULL: {x}\n";
                    val.UpdateText();

                    if ((DateTime.Now - val.UpdateLastChangeTime()).TotalMilliseconds > downloadMaxStaleTime)
                    {
                        val.cts.Cancel();
                        val.displayText = "(Stale)" + val.displayText;
                        val.UpdateText();
                        lock (downloads) { downloads.Remove(key); }
                    }
                }
                else
                {
                    debugDownloads += $"VALUE IS NULL: {key}\n";
                    lock (downloads) { downloads.Remove(key); }
                }
            }

            Debug.WriteLine($"{debugSearches}{debugDownloads}-------------------------------");

            await Task.Delay(displayUpdateDelay);
        }
    }

    static async Task DownloadFile(SearchResponse response, Soulseek.File file, string filePath, CancellationTokenSource? searchCts = null)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(filePath));

        bool transferSet = false;
        var transferOptions = new TransferOptions(
            stateChanged: (state) =>
            {
                if (downloads.ContainsKey(file.Filename) && !transferSet)
                    downloads[file.Filename].transfer = state.Transfer;
            },
            progressUpdated: (progress) =>
            {
                if (downloads.ContainsKey(file.Filename))
                    downloads[file.Filename].bytesTransferred = progress.PreviousBytesTransferred;
            }
        );
        using (var cts = new CancellationTokenSource())
        using (var outputStream = new FileStream(filePath, FileMode.Create))
        {
            lock (downloads) { downloads[file.Filename] = new DownloadInfo(filePath, response, file, cts); }
            WriteLastLine(downloads[file.Filename].displayText);

            try
            {
                await client.DownloadAsync(response.Username, file.Filename, () => Task.FromResult((Stream)outputStream), file.Size, options: transferOptions, cancellationToken: cts.Token);
            }
            catch (Exception e)
            {
                downloads[file.Filename].UpdateText();
                lock (downloads) { downloads.Remove(file.Filename); }
                try
                {
                    if (System.IO.File.Exists(filePath))
                        System.IO.File.Delete(filePath);
                }
                catch { }
                throw;
            }
        }

        searchCts?.Cancel();
        downloads[file.Filename].success = true;
        downloads[file.Filename].UpdateText();
        lock (downloads) { downloads.Remove(file.Filename); }
    }

    static string GetSavePath(Soulseek.File file, Track track)
    {
        string name = track.TrackTitle == "" ? $"{track.UnparsedTitle}" : $"{track.ArtistName} - {track.TrackTitle}";
        char[] invalidChars = Path.GetInvalidFileNameChars();
        foreach (char c in invalidChars)
            name = name.Replace(c, ' ');
        return Path.Combine(outputFolder, $"{name}{Path.GetExtension(file.Filename)}");
    }

    struct Track
    {
        public string UnparsedTitle = "";
        public string Uploader = "";
        public string TrackTitle = "";
        public string ArtistName = "";
        public int Length = -1;
        public Track() { }
    }

    //class ConsoleProcessDisplay
    //{
    //    public DownloadInfo? download;
    //    public SearchInfo? search;
    //    public string displayText = "";
    //    public bool finished = false;

    //    private int rotatingBarState = 0;
    //    private int displayPos = 0;

    //    public ConsoleProcessDisplay(DownloadInfo? download, SearchInfo? search)
    //    {
    //        this.download = download;
    //        this.search = search;
    //        MoveCursorLastLine();
    //        displayPos = Console.CursorTop;
    //    }

    //    public void UpdateText()
    //    {
    //        Console.SetCursorPosition(0, displayPos);
    //        if (finished && (download == null || !download.success))
    //        {
    //            Console.WriteLine(displayText);
    //            return;
    //        }

    //        char[] bars = { '/', '|', '\\', '—' };
    //        rotatingBarState++;
    //        rotatingBarState %= bars.Length;
    //        string bar = bars[rotatingBarState] + " ";

    //        if (download != null)
    //        {
    //            string sampleRate = download.file.SampleRate.HasValue ? $" / {download.file.SampleRate}Hz" : "";
    //            string bitRate = download.file.BitRate.HasValue ? $" / {download.file.BitRate}kbps" : "";
    //            string fileSize = $"{download.file.Size / (float)(1024 * 1024):F1}MB";
    //            displayText = $"{download.response.Username}\\..\\{download.file.Filename.Split('\\').Last()} " +
    //                $"[{download.file.Length}s{sampleRate}{bitRate} / {fileSize}]";
    //            float percentage = download.bytesTransferred / (float)download.file.Size;
    //            //queued = transfer?.State is TransferStates.Remotely or TransferStates.Locally or TransferStates.Queued;
    //            Console.WriteLine($"{(download.success ? bar : "")}[{percentage:P}] {download?.transfer?.State}: {displayText}");
    //        }
    //        else if (search != null)
    //        {
    //            Console.WriteLine($"{bar}Searching for: {search.query.SearchText}");
    //        }
    //    }

    //    public bool IsDone()
    //    {
    //        return download == null && search == null;
    //    }
    //}

    class DownloadInfo
    {
        public string savePath;
        public string displayText = "";
        public int displayPos = 0;
        public int downloadRotatingBarState = 0;
        public Soulseek.File file;
        public Transfer? transfer;
        public SearchResponse response;
        public long bytesTransferred = 0;
        public bool stalled = false;
        public bool queued = false;
        public bool success = false;
        public CancellationTokenSource cts;
        public DateTime startTime = DateTime.Now;

        private DateTime lastChangeTime = DateTime.Now;
        private TransferStates? prevTransferState = null;
        private long prevBytesTransferred = 0;

        public DownloadInfo(string savePath, SearchResponse response, Soulseek.File file, CancellationTokenSource cts)
        {
            this.savePath = savePath;
            this.response = response;
            this.file = file;
            this.cts = cts;
            string sampleRate = file.SampleRate.HasValue ? $" / {file.SampleRate}Hz" : "";
            string bitRate = file.BitRate.HasValue ? $" / {file.BitRate}kbps" : "";
            string fileSize = $"{file.Size / (float)(1024 * 1024):F1}MB";
            displayText = $"{response.Username}\\..\\{file.Filename.Split('\\').Last()} " +
                $"[{file.Length}s{sampleRate}{bitRate} / {fileSize}]";

            MoveCursorLastLine();
            displayPos = Console.CursorTop;
        }

        public void UpdateText()
        {
            Console.SetCursorPosition(0, displayPos);
            char[] bars = { '/', '|', '\\', '—' };
            downloadRotatingBarState++;
            downloadRotatingBarState %= bars.Length;
            string bar = success ? "" : bars[downloadRotatingBarState] + " ";
            float? percentage = bytesTransferred / (float)file.Size;
            string percText = percentage < 0.1 ? $"0{percentage:P}" : $"{percentage:P}";
            queued = transfer?.State.ToString().Contains("Queued") ?? false;
            string state = "NullState";
            if (transfer != null)
            {
                if (queued)
                    state = "Queued";
                else if (transfer.State.ToString().Contains("Completed, "))
                    state = transfer.State.ToString().Replace("Completed, ", "");
                else
                    state = transfer.State.ToString();
            }
            Console.WriteLine($"{bar}[{percText}] {state}: {displayText}");
        }

        public DateTime UpdateLastChangeTime()
        {
            bool changed = prevTransferState != transfer?.State || prevBytesTransferred != bytesTransferred;
            if (changed)
                lastChangeTime= DateTime.Now;
            prevTransferState = transfer?.State;
            prevBytesTransferred = bytesTransferred;
            return lastChangeTime;
        }
    }

    class SearchInfo
    {
        public SearchQuery query;
        public SearchOptions searchOptions;
        public List<SearchResponse> responses;

        public SearchInfo(SearchQuery query, List<SearchResponse> responses, SearchOptions searchOptions)
        {
            this.query = query;
            this.responses = responses;
            this.searchOptions = searchOptions; 
        }
    }

    class FileConditions
    {
        public string Format { get; set; } = "";
        public int LengthTolerance { get; set; } = -1;
        public int MinBitrate { get; set; } = -1;
        public int MaxBitrate { get; set; } = -1;
        public int MaxSampleRate { get; set; } = -1;

        public bool FileSatisfies(Soulseek.File file, int actualLength)
        {
            return FormatSatisfies(file.Filename) && LengthToleranceSatisfies(file, actualLength) && BitrateSatisfies(file) && SampleRateSatisfies(file);
        }

        public bool FileSatisfies(TagLib.File file, int actualLength)
        {
            return FormatSatisfies(file.Name) && LengthToleranceSatisfies(file, actualLength) && BitrateSatisfies(file) && SampleRateSatisfies(file);
        }

        public bool FormatSatisfies(string filename)
        {
            return string.IsNullOrEmpty(Format) || filename.EndsWith(Format, StringComparison.OrdinalIgnoreCase);
        }

        public bool LengthToleranceSatisfies(Soulseek.File file, int actualLength)
        {
            if (LengthTolerance < 0 || actualLength < 0)
                return true;
            return Math.Abs((file.Length ?? -999999) - actualLength) <= LengthTolerance;
        }

        public bool LengthToleranceSatisfies(TagLib.File file, int actualLength)
        {
            int fileLength = (int)file.Properties.Duration.TotalSeconds;
            if (Math.Abs(fileLength - actualLength) <= LengthTolerance)
                return true;
            return false;
        }

        public bool BitrateSatisfies(Soulseek.File file)
        {
            if ((MinBitrate < 0 && MaxBitrate < 0) || file.BitRate == null)
                return true;
            if (MinBitrate >= 0 && file.BitRate.Value < MinBitrate)
                return false;
            if (MaxBitrate >= 0 && file.BitRate.Value > MaxBitrate)
                return false;

            return true;
        }

        public bool BitrateSatisfies(TagLib.File file)
        {
            if ((MinBitrate < 0 && MaxBitrate < 0) || file.Properties.AudioBitrate <= 0)
                return true;
            if (MinBitrate >= 0 && file.Properties.AudioBitrate < MinBitrate)
                return false;
            if (MaxBitrate >= 0 && file.Properties.AudioBitrate > MaxBitrate)
                return false;

            return true;
        }

        public bool SampleRateSatisfies(Soulseek.File file)
        {
            return MaxSampleRate < 0 || file.SampleRate == null || file.SampleRate.Value <= MaxSampleRate;
        }

        public bool SampleRateSatisfies(TagLib.File file)
        {
            return MaxSampleRate < 0 || file.Properties.AudioSampleRate <= MaxSampleRate;
        }
    }

    static List<Track> ParseCsvIntoTrackInfo(string path, string artistCol = "", string trackCol = "", string lengthCol = "", string titleCol = "", string uploaderCol = "", string timeUnit = "s")
    {
        var tracks = new List<Track>();

        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(stream, System.Text.Encoding.UTF8))
        {
            var header = reader.ReadLine();

            var artistIndex = string.IsNullOrEmpty(artistCol) ? -1 : Array.IndexOf(header.Split(','), artistCol);
            var trackIndex = string.IsNullOrEmpty(trackCol) ? -1 : Array.IndexOf(header.Split(','), trackCol);
            var titleIndex = string.IsNullOrEmpty(titleCol) ? -1 : Array.IndexOf(header.Split(','), titleCol);
            var uploaderIndex = string.IsNullOrEmpty(uploaderCol) ? -1 : Array.IndexOf(header.Split(','), uploaderCol);
            var lengthIndex = string.IsNullOrEmpty(lengthCol) ? -1 : Array.IndexOf(header.Split(','), lengthCol);

            var regex = new Regex(",(?=(?:[^\"]*\"[^\"]*\")*[^\"]*$)"); // thank you, ChatGPT.

            while (!reader.EndOfStream)
            {
                var line = reader.ReadLine();
                var values = regex.Split(line);

                var track = new Track();
                if (artistIndex >= 0) track.ArtistName = values[artistIndex].Trim('"').Split(',').First().Trim(' ');
                if (trackIndex >= 0) track.TrackTitle = values[trackIndex].Trim('"');
                if (titleIndex >= 0) track.UnparsedTitle = values[titleIndex].Trim('"');
                if (uploaderIndex >= 0) track.Uploader = values[uploaderIndex].Trim('"');
                if (lengthIndex >= 0 && int.TryParse(values[lengthIndex], out int result) && result > 0)
                {
                    if (timeUnit == "ms")
                        track.Length = result / 1000;
                    else
                        track.Length = result;
                }

                if (track.UnparsedTitle != "" || track.TrackTitle != "") tracks.Add(track);
                else
                    Debug.WriteLine("bad csv line");
            }
        }

        return tracks;
    }


    static int lastLine = 0;
    static void MoveCursorLastLine()
    {
        Console.SetCursorPosition(0, Math.Min(Console.BufferHeight - 1, lastLine));
    }
    static void WriteLastLine(object obj, ConsoleColor? color = null)
    {
        string text = obj?.ToString();
        MoveCursorLastLine();
        if (color != null)
            Console.ForegroundColor = (ConsoleColor)color;
        Console.WriteLine(text);
        if (color != null)
            Console.ResetColor();
        lastLine = Math.Max(Console.CursorTop, lastLine + 1);
    }
    static bool IsMusicFile(string fileName)
    {
        var musicExtensions = new string[] { ".mp3", ".wav", ".flac", ".ogg", ".aac", ".wma", ".m4a", ".alac", ".ape", ".dsd", ".dff", ".dsf", ".ogg", ".opus" };
        var extension = Path.GetExtension(fileName).ToLower();
        return musicExtensions.Contains(extension);
    }
    static bool FileExistsInCollection(string searchName, int length, FileConditions conditions, IEnumerable<string> collection, out string foundPath)
    {
        char[] invalidChars = Path.GetInvalidFileNameChars();
        foreach (char c in invalidChars)
            searchName = searchName.Replace(c.ToString(), "");
        searchName = searchName.Replace(" ", "");

        Debug.WriteLine($"total: {collection.Count()}");

        var matchingFiles = collection.Where(fileName => fileName.Replace(" ", "").Contains(searchName, StringComparison.OrdinalIgnoreCase));

        Debug.WriteLine($"matches: {matchingFiles.Count()}");

        if (matchingFiles.Any())
        {
            foundPath = matchingFiles.First();
            return true;
        }
        else if (searchName.Count(c => c == '-') == 1)
        {
            searchName = searchName.Split('-')[1];
            matchingFiles = collection.Where(fileName => fileName.Replace(" ", "").Contains(searchName, StringComparison.OrdinalIgnoreCase));
            if (matchingFiles.Any())
            {
                foundPath = matchingFiles.First();
                return true;
            }
        }
        foundPath = null;
        return false;
    }

    static void WriteLineOutputFile(string line)
    {
        using (var fileStream = new FileStream(failsFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        using (var streamWriter = new StreamWriter(fileStream, System.Text.Encoding.UTF8))
        {
            streamWriter.WriteLine(line);
        }
    }
    static void WriteAllLinesOutputFile(string text)
    {
        using (var fileStream = new FileStream(failsFilePath, FileMode.Create, FileAccess.Write, FileShare.Read))
        using (var streamWriter = new StreamWriter(fileStream, System.Text.Encoding.UTF8))
        {
            streamWriter.WriteLine(text);
        }

    }
    static string[] ParseCommand(string cmd)
    {
        Debug.WriteLine(cmd);
        string pattern = @"(""[^""]*""|\S+)";
        MatchCollection matches = Regex.Matches(cmd, pattern);
        var args = new string[matches.Count];
        for (int i = 0; i < matches.Count; i++)
            args[i] = matches[i].Value.Trim('"');
        return args;
    }
}