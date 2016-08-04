// Copyright (c) Microsoft. All rights reserved.

namespace Microsoft.VisualStudio.TestPlatform.Common.Logging
{
    using Microsoft.VisualStudio.TestPlatform.Common.Interfaces;
    using Microsoft.VisualStudio.TestPlatform.ObjectModel;
    using Microsoft.VisualStudio.TestPlatform.ObjectModel.Client;
    using Microsoft.VisualStudio.TestPlatform.ObjectModel.Logging;
    using System;
    using System.Collections.Generic;
    using System.Diagnostics.CodeAnalysis;
    using System.Globalization;
    using System.Linq;
    using CommonResources = Microsoft.VisualStudio.TestPlatform.Common.Resources;

    /// <summary>
    /// Responsible for managing logger extensions and broadcasting results
    /// and error/warning/informational messages to them.
    /// </summary>
    internal class TestLoggerManager : ITestDiscoveryEventsRegistrar, ITestRunEventsRegistrar, IDisposable
    {
        #region Fields

        private static readonly object Synclock = new object();
        private static TestLoggerManager testLoggerManager;

        /// <summary>
        /// Test Logger Events instance which will be passed to loggers when they are initialized.
        /// </summary>
        private InternalTestLoggerEvents loggerEvents;

        /// <summary>
        /// Used to keep track of which loggers have been initialized.
        /// </summary>
        private HashSet<String> initializedLoggers = new HashSet<String>();

        /// <summary>
        /// Keeps track if we are disposed.
        /// </summary>
        private bool isDisposed = false;

        /// <summary>
        /// Run request that we have registered for events on.  Used when
        /// disposing to unregister for the events.
        /// </summary>
        private ITestRunRequest runRequest = null;

        /// <summary>
        /// Gets an instance of the logger.
        /// </summary>
        private IMessageLogger messageLogger;

        private TestLoggerExtensionManager testLoggerExtensionManager;
        private IDiscoveryRequest discoveryRequest;
        
        #endregion

        #region Constructor

        /// <summary>
        /// Default constructor.
        /// </summary>
        protected TestLoggerManager()
        {
            this.messageLogger = TestSessionMessageLogger.Instance;
            this.testLoggerExtensionManager = TestLoggerExtensionManager.Create(messageLogger);
            this.loggerEvents = new InternalTestLoggerEvents((TestSessionMessageLogger)messageLogger);
        }

        /// <summary>
        /// Gets the instance.
        /// </summary>
        public static TestLoggerManager Instance
        {
            get
            {
                if (testLoggerManager == null)
                {
                    lock (Synclock)
                    {
                        if (testLoggerManager == null)
                        {
                            testLoggerManager = new TestLoggerManager();
                        }
                    }
                }
                return testLoggerManager;
            }
            protected set
            {
                testLoggerManager = value;
            }
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets the logger events.
        /// </summary>
        public TestLoggerEvents LoggerEvents
        {
            get
            {
                return this.loggerEvents;
            }
        }

        /// <summary>
        /// Gets the initialized loggers.
        /// </summary>
        /// This property is added to assist in testing
        protected HashSet<string> InitializedLoggers
        {
            get
            {
                return this.initializedLoggers;
            }
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Adds the logger with the specified URI and parameters.
        /// For ex. TfsPublisher takes parameters such as  Platform, Flavor etc.
        /// </summary>
        /// <param name="uri">URI of the logger to add.</param>
        /// <param name="parameters">Logger parameters.</param>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage", "CA2234:PassSystemUriObjectsInsteadOfStrings", Justification = "Case insensitive needs to be supported "), SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes", Justification = "Third party loggers could potentially throw all kinds of exceptions.")]
        public void AddLogger(Uri uri, Dictionary<string, string> parameters)
        {
            ValidateArg.NotNull<Uri>(uri, "uri");

            this.CheckDisposed();

            // If the logger has already been initialized just return.
            if (this.initializedLoggers.Contains(uri.AbsoluteUri, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }
            this.initializedLoggers.Add(uri.AbsoluteUri);

            // Look up the extension and initialize it if one is found.
            var extensionManager = this.testLoggerExtensionManager;
            var logger = extensionManager.TryGetTestExtension(uri.AbsoluteUri);

            if (logger != null)
            {
                try
                {
                    if (logger.Value is ITestLoggerWithParameters)
                    {
                        ((ITestLoggerWithParameters)logger.Value).Initialize(this.loggerEvents, this.UpdateLoggerParamters(parameters));
                    }
                    else
                    {
                        // todo Read Output Directory from RunSettings
                        ((ITestLogger)logger.Value).Initialize(this.loggerEvents, null);
                    }
                }
                catch (Exception e)
                {
                    this.messageLogger.SendMessage(
                        TestMessageLevel.Error,
                        string.Format(
                            CultureInfo.CurrentUICulture,
                            CommonResources.LoggerInitializationError,
                            logger.Metadata.ExtensionUri,
                            e));
                }
            }
            else
            {
                throw new InvalidOperationException(
                    String.Format(
                        CultureInfo.CurrentUICulture,
                        CommonResources.LoggerNotFound,
                        uri.OriginalString));
            }
        }
        
        /// <summary>
        /// Tries to get uri of the logger corresponding to the friendly name. If no such logger exists return null.
        /// </summary>
        /// <param name="friendlyName">The friendly Name.</param>
        /// <param name="loggerUri">The logger Uri.</param>
        /// <returns><see cref="bool"/></returns>
        public bool TryGetUriFromFriendlyName(string friendlyName, out string loggerUri)
        {
            var extensionManager = this.testLoggerExtensionManager;
            foreach (var extension in extensionManager.TestExtensions)
            {
                if (string.Compare(friendlyName, extension.Metadata.FriendlyName, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    loggerUri = extension.Metadata.ExtensionUri;
                    return true;
                }
            }

            loggerUri = null;
            return false;
        }
        
        /// <summary>
        /// Registers to receive events from the provided test run request.
        /// These events will then be broadcast to any registered loggers.
        /// </summary>
        /// <param name="testRunRequest">The run request to register for events on.</param>
        public void RegisterTestRunEvents(ITestRunRequest testRunRequest)
        {
            ValidateArg.NotNull<ITestRunRequest>(testRunRequest, "testRunRequest");

            this.CheckDisposed();

            // Keep track of the run requests so we can unregister for the
            // events when disposed.
            this.runRequest = testRunRequest;

            // Redirect the events to the InternalTestLoggerEvents
            testRunRequest.TestRunMessage += this.TestRunMessageHandler;
            testRunRequest.OnRunStatsChange += this.TestRunStatsChangedHandler;
            testRunRequest.OnRunCompletion += this.TestRunCompleteHandler;
            testRunRequest.DataCollectionMessage += this.DataCollectionMessageHandler;
        }

        /// <summary>
        /// Registers to receive discovery events from discovery request.
        /// These events will then be broadcast to any registered loggers.
        /// </summary>
        /// <param name="discoveryRequest">The discovery request to register for events on.</param>
        public void RegisterDiscoveryEvents(IDiscoveryRequest discoveryRequest)
        {
            ValidateArg.NotNull<IDiscoveryRequest>(discoveryRequest, "discoveryRequest");

            this.CheckDisposed();
            this.discoveryRequest = discoveryRequest;
            discoveryRequest.OnDiscoveryMessage += this.DiscoveryMessageHandler;
        }
        
        /// <summary>
        /// Unregisters the events from the test run request. 
        /// </summary>
        /// <param name="testRunRequest">The run request from which events should be unregistered.</param>
        public void UnregisterTestRunEvents(ITestRunRequest testRunRequest)
        {
            ValidateArg.NotNull<ITestRunRequest>(testRunRequest, "testRunRequest");

            testRunRequest.TestRunMessage -= this.TestRunMessageHandler;
            testRunRequest.OnRunStatsChange -= this.TestRunStatsChangedHandler;
            testRunRequest.OnRunCompletion -= this.TestRunCompleteHandler;
            this.runRequest.DataCollectionMessage -= this.DiscoveryMessageHandler;
        }
        
        /// <summary>
        /// Unregister the events from the discovery request.
        /// </summary>
        /// <param name="discoveryRequest">The discovery request from which events should be unregistered.</param>
        public void UnregisterDiscoveryEvents(IDiscoveryRequest discoveryRequest)
        {
            ValidateArg.NotNull<IDiscoveryRequest>(discoveryRequest, "discoveryRequest");
            discoveryRequest.OnDiscoveryMessage -= this.DiscoveryMessageHandler;
        }

        /// <summary>
        /// Enables sending of events to the loggers which are registered.
        /// </summary>
        /// <remarks>
        /// By default events are disabled and will not be raised until this method is called.
        /// This is done because during logger initialization, errors could be sent and we do not
        /// want them broadcast out to the loggers until all loggers have been enabled.  Without this
        /// all loggers would not receive the errors which were sent prior to initialization finishing.
        /// </remarks>
        public void EnableLogging()
        {
            this.CheckDisposed();
            this.loggerEvents.EnableEvents();
        }

        /// <summary>
        /// Ensure that all pending messages are sent to the loggers.
        /// </summary>
        public void Dispose()
        {
            this.Dispose(true);

            // Use SupressFinalize in case a subclass
            // of this type implements a finalizer.
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Sends the error message to all registered loggers.
        /// This is required so that out of test run execution errors 
        /// can also mark test run test run failure.
        /// </summary>
        /// <param name="e">
        /// The e.
        /// </param>
        public void SendTestRunError(TestRunMessageEventArgs e)
        {
            this.TestRunMessageHandler(null, e);
        }

        /// <summary>
        /// Ensure that all pending messages are sent to the loggers.
        /// </summary>
        /// <param name="disposing">
        /// The disposing.
        /// </param>
        protected virtual void Dispose(bool disposing)
        {
            if (!this.isDisposed)
            {
                if (disposing)
                {
                    // Unregister from runrequests.
                    if (this.runRequest != null)
                    {
                        this.runRequest.TestRunMessage -= this.TestRunMessageHandler;
                        this.runRequest.OnRunStatsChange -= this.TestRunStatsChangedHandler;
                        this.runRequest.OnRunCompletion -= this.TestRunCompleteHandler;
                        this.runRequest.DataCollectionMessage -= this.DiscoveryMessageHandler;
                    }

                    if (this.discoveryRequest != null)
                    {
                        this.discoveryRequest.OnDiscoveryMessage -= this.DiscoveryMessageHandler;
                    }

                    this.loggerEvents.Dispose();
                }

                this.isDisposed = true;
            }
        }

        #endregion

        #region Private Members

        /// <summary>
        /// Populates user supplied and default logger parameters.
        /// </summary>
        private Dictionary<string, string> UpdateLoggerParamters(Dictionary<string, string> parameters)
        {
            var loggerParams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (parameters != null)
            {
                loggerParams = new Dictionary<string, string>(parameters, StringComparer.OrdinalIgnoreCase);
            }

            // Add default logger parameters...
            // todo Read Output Directory from RunSettings
            loggerParams[DefaultLoggerParameterNames.TestRunDirectory] = null;
            return loggerParams;
        }

        private void CheckDisposed()
        {
            if (this.isDisposed)
            {
                throw new ObjectDisposedException(typeof(TestLoggerManager).FullName);
            }
        }

        #region Event Handlers

        /// <summary>
        /// Called when a test run message is received.
        /// </summary>
        private void TestRunMessageHandler(object sender, TestRunMessageEventArgs e)
        {
            this.loggerEvents.RaiseMessage(e);
        }

        /// <summary>
        /// Called when a test run stats are changed.
        /// </summary>
        private void TestRunStatsChangedHandler(object sender, TestRunChangedEventArgs e)
        {
            foreach (TestResult result in e.NewTestResults)
            {
                this.loggerEvents.RaiseTestResult(new TestResultEventArgs(result));
            }
        }

        /// <summary>
        /// Called when a test run is complete.
        /// </summary>
        private void TestRunCompleteHandler(object sender, TestRunCompleteEventArgs e)
        {
            this.loggerEvents.CompleteTestRun(e.TestRunStatistics, e.IsCanceled, e.IsAborted, e.Error, e.AttachmentSets, e.ElapsedTimeInRunningTests);
        }


        /// <summary>
        /// Called when data collection message is received.
        /// </summary>
        private void DataCollectionMessageHandler(object sender, DataCollectionMessageEventArgs e)
        {
            string message;
            if (null == e.Uri)
            {
                // Message from data collection framework.
                message = string.Format(CultureInfo.CurrentCulture, CommonResources.DataCollectionMessageFormat, e.Message);
            }
            else
            {
                // Message from individual data collector.
                message = string.Format(CultureInfo.CurrentCulture, CommonResources.DataCollectorMessageFormat, e.FriendlyName, e.Message);
            }
            this.TestRunMessageHandler(sender, new TestRunMessageEventArgs(e.Level, message));
        }


        /// <summary>
        /// Send discovery message to all registered listeners.
        /// </summary>
        private void DiscoveryMessageHandler(object sender, TestRunMessageEventArgs e)
        {
            this.loggerEvents.RaiseMessage(e);
        }
        #endregion

        #endregion
    }
}