﻿using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using TptMain.InDesign;
using TptMain.Models;
using TptMain.ParatextProjects;
using TptMain.Toolbox;
using TptMain.Util;

namespace TptMain.Jobs
{
    /// <summary>
    /// Job manager for handling typesetting preview job request management and execution.
    /// </summary>
    public class JobManager : IDisposable, IJobManager
    {
        /// <summary>
        /// Type-specific logger (injected).
        /// </summary>
        private readonly ILogger<JobManager> _logger;

        /// <summary>
        /// Job preview context (persistence; injected).
        /// </summary>
        private readonly TptServiceContext _tptServiceContext;

        /// <summary>
        /// Preview Manager (injected).
        /// </summary>
        private readonly IPreviewManager _previewManager;

        /// <summary>
        /// Template manager.
        /// </summary>
        private readonly TemplateManager _templateManager;

        /// <summary>
        /// Job Validation service for ensuring feasible and authorized jobs.
        /// </summary>
        private readonly IPreviewJobValidator _jobValidator;

        /// <summary>
        /// Paratext Project service used to get information related to local Paratext projects.
        /// </summary>
        private readonly ParatextProjectService _paratextProjectService;

        /// <summary>
        /// Job scheduler service (injected).
        /// </summary>
        private readonly JobScheduler _jobScheduler;

        /// <summary>
        /// Template (IDML) storage directory (configured).
        /// </summary>
        private readonly DirectoryInfo _idmlDirectory;

        /// <summary>
        /// Tagged Text (IDTT) storage directory (configured).
        /// </summary>
        private readonly DirectoryInfo _idttDirectory;

        /// <summary>
        /// Preview PDF storage directory (configured).
        /// </summary>
        private readonly DirectoryInfo _pdfDirectory;

        /// <summary>
        /// Preview zip storage directory (configured).
        /// </summary>
        private readonly DirectoryInfo _zipDirectory;

        /// <summary>
        /// Max document (IDML, PDF) age, in seconds (configured).
        /// </summary>
        private readonly int _maxDocAgeInSec;

        /// <summary>
        /// Job expiration check timer.
        /// </summary>
        private readonly Timer _jobCheckTimer;

        /// <summary>
        /// PDF expiration check timer.
        /// </summary>
        private readonly Timer _docCheckTimer;

        /// <summary>
        /// Template (IDML) storage directory.
        /// </summary>
        public DirectoryInfo IdmlDirectory => _idmlDirectory;

        /// <summary>
        /// Basic ctor.
        /// </summary>
        /// <param name="logger">Type-specific logger (required).</param>
        /// <param name="configuration">System configuration (required).</param>
        /// <param name="tptServiceContext">Database context (persistence; required).</param>
        /// <param name="scriptRunner">Script runner (required).</param>
        /// <param name="templateManager">Template manager (required).</param>
        /// <param name="jobValidator">Preview Job Validator used for ensuring job feasibility and authorization on projects (required).</param>
        /// <param name="paratextProjectService">Paratext Project service for getting information related to local Paratext projects. (required).</param>
        /// <param name="jobScheduler">Job scheduler (required).</param>
        public JobManager(
            ILogger<JobManager> logger,
            IConfiguration configuration,
            TptServiceContext tptServiceContext,
            IPreviewManager previewManager,
            TemplateManager templateManager,
            IPreviewJobValidator jobValidator,
            ParatextProjectService paratextProjectService,
            JobScheduler jobScheduler)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _ = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _tptServiceContext = tptServiceContext ?? throw new ArgumentNullException(nameof(tptServiceContext));
            _previewManager = previewManager ?? throw new ArgumentNullException(nameof(previewManager));
            _templateManager = templateManager ?? throw new ArgumentNullException(nameof(templateManager));
            _jobValidator = jobValidator ?? throw new ArgumentNullException(nameof(jobValidator));
            _paratextProjectService = paratextProjectService ?? throw new ArgumentNullException(nameof(paratextProjectService));
            _jobScheduler = jobScheduler ?? throw new ArgumentNullException(nameof(jobScheduler));

            _idmlDirectory = new DirectoryInfo(configuration[ConfigConsts.IdmlDocDirKey]
                                               ?? throw new ArgumentNullException(ConfigConsts.IdmlDocDirKey));
            _idttDirectory = new DirectoryInfo(configuration[ConfigConsts.IdttDocDirKey]
                                               ?? throw new ArgumentNullException(ConfigConsts.IdttDocDirKey));
            _pdfDirectory = new DirectoryInfo(configuration[ConfigConsts.PdfDocDirKey]
                                              ?? throw new ArgumentNullException(ConfigConsts.PdfDocDirKey));
            _zipDirectory = new DirectoryInfo(configuration[ConfigConsts.ZipDocDirKey]
                                              ?? throw new ArgumentNullException(ConfigConsts.ZipDocDirKey));
            _maxDocAgeInSec = int.Parse(configuration[ConfigConsts.MaxDocAgeInSecKey]
                                        ?? throw new ArgumentNullException(ConfigConsts.MaxDocAgeInSecKey));
            _jobCheckTimer = new Timer((stateObject) => { CheckPreviewJobs(); }, null,
                 TimeSpan.FromSeconds(MainConsts.TIMER_STARTUP_DELAY_IN_SEC),
                 TimeSpan.FromSeconds(_maxDocAgeInSec / MainConsts.MAX_AGE_CHECK_DIVISOR));
            _docCheckTimer = new Timer((stateObject) => { CheckDocFiles(); }, null,
                 TimeSpan.FromSeconds(MainConsts.TIMER_STARTUP_DELAY_IN_SEC),
                 TimeSpan.FromSeconds(_maxDocAgeInSec / MainConsts.MAX_AGE_CHECK_DIVISOR));

            if (!Directory.Exists(_pdfDirectory.FullName))
            {
                Directory.CreateDirectory(_pdfDirectory.FullName);
            }

            if (!Directory.Exists(_zipDirectory.FullName))
            {
                Directory.CreateDirectory(_zipDirectory.FullName);
            }

            _logger.LogDebug("JobManager()");
        }

        /// <summary>
        /// Iterate through PDFs and clean up old ones.
        /// </summary>
        public virtual void CheckPreviewJobs()
        {
            try
            {
                lock (_tptServiceContext)
                {
                    _logger.LogDebug("Checking preview jobs...");

                    var checkTime = DateTime.UtcNow.Subtract(TimeSpan.FromSeconds(_maxDocAgeInSec));
                    IList<PreviewJob> toRemove = new List<PreviewJob>();

                    foreach (var jobItem in _tptServiceContext.PreviewJobs)
                    {
                        var refTime = jobItem.DateCompleted
                            ?? jobItem.DateCancelled
                            ?? jobItem.DateStarted
                            ?? jobItem.DateSubmitted;

                        if (refTime != null
                            && refTime < checkTime)
                        {
                            toRemove.Add(jobItem);
                        }
                    }
                    if (toRemove.Count > 0)
                    {
                        _tptServiceContext.PreviewJobs.RemoveRange(toRemove);
                        _tptServiceContext.SaveChanges();
                    }

                    _logger.LogDebug("...Preview jobs checked.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Can't check preview jobs.");
            }
        }

        /// <summary>
        /// Iterate through generated preview files and clean up old ones.
        /// </summary>
        private void CheckDocFiles()
        {
            try
            {
                _logger.LogDebug("Checking document files...");

                DeleteOldFiles(_pdfDirectory.FullName, $"{MainConsts.PREVIEW_FILENAME_PREFIX}*.*");
                DeleteOldFiles(_idmlDirectory.FullName, $"{MainConsts.PREVIEW_FILENAME_PREFIX}*.*");
                DeleteOldFiles(_zipDirectory.FullName, $"{MainConsts.PREVIEW_FILENAME_PREFIX}*.*");

                _logger.LogDebug("...Document files checked.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Can't check document files.");
            }
        }

        /// <summary>
        /// Delete files that exceed the configured maximum document age in seconds (_maxDocAgeInSec). The files are based on a provided directory and file search pattern.
        /// </summary>
        /// <param name="directory">The source directory containing the files we want to assess for deletion. (required)</param>
        /// <param name="fileSearchPattern">A file search pattern for filtering out files assess for deletion. EG: "preview-*.zip" (required)</param>
        private void DeleteOldFiles(string directory, string fileSearchPattern)
        {
            // Validate inputs
            _ = directory ?? throw new ArgumentNullException(nameof(directory));
            _ = fileSearchPattern ?? throw new ArgumentNullException(nameof(fileSearchPattern));

            var checkTime = DateTime.UtcNow.Subtract(TimeSpan.FromSeconds(_maxDocAgeInSec));

            foreach (var fileItem in Directory.EnumerateFiles(directory, fileSearchPattern))
            {
                var foundFile = new FileInfo(fileItem);
                if (foundFile.CreationTimeUtc < checkTime)
                {
                    try
                    {
                        foundFile.Delete();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, $"Can't delete file (will retry): {fileItem}.");
                    }
                }
            }
        }

        /// <summary>
        /// Create and schedule a new preview job.
        /// </summary>
        /// <param name="inputJob">Preview job to be added (required).</param>
        /// <param name="outputJob">Persisted preview job if created, null otherwise.</param>
        /// <returns>True if job created successfully, false otherwise.</returns>
        public virtual bool TryAddJob(PreviewJob inputJob, out PreviewJob outputJob)
        {
            _logger.LogDebug($"TryAddJob() - inputJob.Id={inputJob.Id}.");
            if (inputJob.Id != null
                || inputJob.BibleSelectionParams.ProjectName == null
                || inputJob.BibleSelectionParams.ProjectName.Any(charItem => !char.IsLetterOrDigit(charItem)))
            {
                outputJob = null;
                return false;
            }
            this.InitPreviewJob(inputJob);

            lock (_tptServiceContext)
            {
                _tptServiceContext.PreviewJobs.Add(inputJob);
                _tptServiceContext.SaveChanges();

                _jobScheduler.AddEntry(
                    new JobWorkflow(
                        _logger,
                        this,
                        _previewManager,
                        _templateManager,
                        _jobValidator,
                        _paratextProjectService,
                        inputJob
                        )
                    );

                outputJob = inputJob;
                return true;
            }
        }

        /// <summary>
        /// Initializes a new preview job with defaults.
        /// </summary>
        /// <param name="previewJob">Preview job to initialize (required).</param>
        private void InitPreviewJob(PreviewJob previewJob)
        {
            // identifying information
            previewJob.Id = Guid.NewGuid().ToString();
            previewJob.BibleSelectionParams.Id = Guid.NewGuid().ToString();
            previewJob.TypesettingParams.Id = Guid.NewGuid().ToString();
            previewJob.State.Add(new PreviewJobState(JobStateEnum.Submitted));

            // project defaults
            previewJob.TypesettingParams.FontSizeInPts ??= MainConsts.ALLOWED_FONT_SIZE_IN_PTS.Default;
            previewJob.TypesettingParams.FontLeadingInPts ??= MainConsts.ALLOWED_FONT_LEADING_IN_PTS.Default;
            previewJob.TypesettingParams.PageWidthInPts ??= MainConsts.ALLOWED_PAGE_WIDTH_IN_PTS.Default;
            previewJob.TypesettingParams.PageHeightInPts ??= MainConsts.ALLOWED_PAGE_HEIGHT_IN_PTS.Default;
            previewJob.TypesettingParams.PageHeaderInPts ??= MainConsts.ALLOWED_PAGE_HEADER_IN_PTS.Default;
            previewJob.TypesettingParams.BookFormat ??= MainConsts.DEFAULT_BOOK_FORMAT;
        }

        /// <summary>
        /// Delete a preview job.
        /// </summary>
        /// <param name="jobId">Job ID to delete (required).</param>
        /// <param name="outputJob">The deleted job if found, null otherwise.</param>
        /// <returns>True if successful, false otherwise.</returns>
        public virtual bool TryDeleteJob(string jobId, out PreviewJob outputJob)
        {
            _logger.LogDebug($"TryDeleteJob() - jobId={jobId}.");
            lock (_tptServiceContext)
            {
                if (TryGetJob(jobId, out var foundJob))
                {
                    _jobScheduler.RemoveEntry(foundJob.Id);

                    _tptServiceContext.PreviewJobs.Remove(foundJob);
                    _tptServiceContext.SaveChanges();

                    outputJob = foundJob;
                    return true;
                }
                else
                {
                    outputJob = null;
                    return false;
                }
            }
        }

        /// <summary>
        /// Update preview job.
        /// </summary>
        /// <param name="nextJob">Preview job to update (required).</param>
        /// <returns>True if successful, false otherwise.</returns>
        public virtual bool TryUpdateJob(PreviewJob nextJob)
        {
            _logger.LogDebug($"TryUpdateJob() - nextJob={nextJob.Id}.");
            lock (_tptServiceContext)
            {
                if (IsJobId(nextJob.Id))
                {
                    _tptServiceContext.Entry(nextJob).State = EntityState.Modified;
                    _tptServiceContext.SaveChanges();

                    return true;
                }
                else
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Retrieves a preview job.
        /// </summary>
        /// <param name="jobId">Job ID to retrieve (required).</param>
        /// <param name="previewJob">Retrieved preview job if found, null otherwise.</param>
        /// <returns>True if successful, false otherwise.</returns>
        public virtual bool TryGetJob(string jobId, out PreviewJob previewJob)
        {
            _logger.LogDebug($"TryGetJob() - jobId={jobId}.");
            lock (_tptServiceContext)
            {
                previewJob = _tptServiceContext.PreviewJobs.Find(jobId);
                return (previewJob != null);
            }
        }

        /// <summary>
        /// Checks whether a given job exists, based on ID.
        /// </summary>
        /// <param name="jobId">Job ID to retrieve (required).</param>
        /// <returns>True if job exists, false otherwise.</returns>
        public virtual bool IsJobId(string jobId)
        {
            _logger.LogDebug($"IsJobId() - jobId={jobId}.");
            lock (_tptServiceContext)
            {
                return _tptServiceContext.PreviewJobs.Find(jobId) != null;
            }
        }

        /// <summary>
        /// Retrieves file stream for preview job PDF.
        /// </summary>
        /// <param name="jobId">Job ID of preview to retrieve file for.</param>
        /// <param name="fileStream">File stream if found, null otherwise.</param>
        /// <param name="archive">Whether or not to return an archive of all the typesetting files or just the PDF itself (optional). 
        /// True: all typesetting files zipped in an archive. False: Output only the preview PDF itself. . Default: false.</param>
        /// <returns>True if successful, false otherwise.</returns>
        public virtual bool TryGetPreviewStream(string jobId, out FileStream fileStream, bool archive)
        {
            _logger.LogDebug($"TryGetPreviewStream() - jobId={jobId}.");
            lock (_tptServiceContext)
            {
                if (!TryGetJob(jobId, out var previewJob))
                {
                    fileStream = null;
                    return false;
                }
                else
                {
                    try
                    {
                        if (archive)
                        {
                            _logger.LogDebug($"Preparing archive for job: {jobId}");

                            // readable string format of book format used for find associated files in directories.
                            var bookFormatStr = Enum.GetName(typeof(BookFormat), BookFormat.cav);

                            /// Archive all typesetting preview files
                            // Create the initial zip file.
                            var zipFilePath = Path.Combine(_zipDirectory.FullName, $"{MainConsts.PREVIEW_FILENAME_PREFIX}{previewJob.Id}.zip");

                            // Return file if it already exists
                            if (File.Exists(zipFilePath))
                            {
                                // Return a stream of the file
                                fileStream = File.Open(
                                    zipFilePath,
                                    FileMode.Open, FileAccess.Read);

                                // Return that we have the necessary file
                                return true;
                            }

                            var zip = ZipFile.Open(zipFilePath, ZipArchiveMode.Create);

                            //IDML
                            //"{{Properties::Docs::IDML::Directory}}\preview-{{PreviewJob::id}}.idml"
                            var idmlDirectory = $@"{_idmlDirectory}";
                            var idmlFilePattern = $"{MainConsts.PREVIEW_FILENAME_PREFIX}{previewJob.Id}.idml";
                            AddFilesToZip(zip, idmlDirectory, idmlFilePattern, "IDML");

                            //IDTT / TXT
                            //"{{Properties::Docs::IDTT::Directory}}\{{PreviewJob::bookFormat}}\{{PreviewJob::projectName}}\book*.txt"
                            var idttDirectory = Path.Combine(_idttDirectory.FullName, bookFormatStr, previewJob.BibleSelectionParams.ProjectName);
                            var idttFilePattern = "book*.txt";
                            AddFilesToZip(zip, idttDirectory, idttFilePattern, "IDTT");

                            //INDD
                            //"{{Properties::Docs::IDML::Directory}}\preview-{{PreviewJob::id}}-*.indd"
                            var inddDirectory = _idmlDirectory.FullName;
                            var inddFilePattern = $"{ MainConsts.PREVIEW_FILENAME_PREFIX}{ previewJob.Id}-*.indd";
                            AddFilesToZip(zip, inddDirectory, inddFilePattern, "INDD");

                            //INDB
                            //"{{Properties::Docs::IDML::Directory}}\preview-{{PreviewJob::id}}.indb"
                            var indbDirectory = _idmlDirectory.FullName;
                            var indbFilePattern = $"{MainConsts.PREVIEW_FILENAME_PREFIX}{previewJob.Id}.indb";
                            AddFilesToZip(zip, indbDirectory, indbFilePattern, "INDB");

                            //PDF
                            //"{{Properties::Docs::PDF::Directory}}\preview-{{PreviewJob::id}}.pdf"
                            var pdfDirectory = _pdfDirectory.FullName;
                            var pdfFilePattern = $"{MainConsts.PREVIEW_FILENAME_PREFIX}{previewJob.Id}.pdf";
                            AddFilesToZip(zip, pdfDirectory, pdfFilePattern, "PDF");

                            // finalize the zip file writing
                            zip.Dispose();

                            // Return a stream of the file
                            fileStream = File.Open(
                                zipFilePath,
                                FileMode.Open, FileAccess.Read);

                            // Return that we have the necessary file
                            return true;
                        }
                        else
                        {
                            // Return the preview PDF
                            fileStream = File.Open(
                                Path.Combine(_pdfDirectory.FullName, $"preview-{previewJob.Id}.pdf"),
                                FileMode.Open, FileAccess.Read);
                            return true;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, $"Can't open file for job: {jobId}");

                        fileStream = null;
                        return false;
                    }
                }
            }
        }

        /// <summary>
        /// A utility function for adding files to a directory in a zip file. The files added are based on a source directory and a file search pattern using wildcards.
        /// </summary>
        /// <param name="zipArchive">The zip file to append files to. (required)</param>
        /// <param name="sourceDirectoryPath">The source directory containing the files we want to add to the zip. (required)</param>
        /// <param name="sourceFileSearchPattern">A file search pattern for filtering out wanted files in the source directory. EG: "preview-*.*" (required)</param>
        /// <param name="targetDirectoryInZip">A directory inside of the zip file to stick the found files. (required)</param>
        private void AddFilesToZip(ZipArchive zipArchive, string sourceDirectoryPath, string sourceFileSearchPattern, string targetDirectoryInZip)
        {
            // Validate inputs
            _ = zipArchive ?? throw new ArgumentNullException(nameof(zipArchive));
            _ = sourceDirectoryPath ?? throw new ArgumentNullException(nameof(sourceDirectoryPath));
            _ = sourceFileSearchPattern ?? throw new ArgumentNullException(nameof(sourceFileSearchPattern));
            _ = targetDirectoryInZip ?? throw new ArgumentNullException(nameof(targetDirectoryInZip));

            // ensure we address missing directories
            if (!Directory.Exists(sourceDirectoryPath))
            {
                _logger.LogWarning($"There's no directory at '${sourceDirectoryPath}'");
                return;
            }

            // Add files found using provided directory and file search pattern to the archive.
            var foundFiles = Directory.GetFiles(sourceDirectoryPath, sourceFileSearchPattern);
            foreach (var file in foundFiles)
            {
                // Add the entry for each file
                zipArchive.CreateEntryFromFile(file, Path.Combine(targetDirectoryInZip, Path.GetFileName(file)), CompressionLevel.Optimal);
            }

            if (foundFiles.Length <= 0)
            {
                _logger.LogWarning($"There were no files found in the directory '${sourceDirectoryPath}' using the file search pattern '${sourceFileSearchPattern}'");
            }
        }

        /// <summary>
        /// Disposes of class resources.
        /// </summary>
        public void Dispose()
        {
            _logger.LogDebug("Dispose().");

            _jobScheduler.Dispose();
            _jobCheckTimer.Dispose();
            _docCheckTimer.Dispose();
        }
    }
}