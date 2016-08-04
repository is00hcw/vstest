// Copyright (c) Microsoft. All rights reserved.

namespace Microsoft.VisualStudio.TestPlatform.Common.ExtensionFramework.Utilities
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;

    using ObjectModel;

    /// <summary>
    /// The test settings provider plugin information.
    /// </summary>
    internal class TestSettingsProviderPluginInformation : TestPluginInformation
    {
        /// <summary>
        /// Default constructor
        /// </summary>
        /// <param name="testSettingsProviderType"> The test Settings Provider Type. </param>
        public TestSettingsProviderPluginInformation(Type testSettingsProviderType)
            : base(testSettingsProviderType)
        {
            if (testSettingsProviderType != null)
            {
                this.SettingsName = GetTestSettingsName(testSettingsProviderType);
            }
        }

        /// <summary>
        /// Gets data value identifying the test plugin
        /// </summary>
        public override string IdentifierData
        {
            get
            {
                return this.SettingsName;
            }
        }

        /// <summary>
        /// Metadata for the test plugin
        /// </summary>
        public override ICollection<Object> Metadata
        {
            get
            {
                return new object[] { this.SettingsName };
            }
        }

        /// <summary>
        /// Gets name of test settings supported by plugin.
        /// </summary>
        public string SettingsName
        {
            get;
            private set;
        }

        /// <summary>
        /// Helper to get the test settings name from SettingsNameAttribute on test setting provider plugin.
        /// </summary>
        /// <param name="testSettingsProviderType">Data type of test setting provider</param>
        /// <returns>Test settings name supported by plugin</returns>
        private static string GetTestSettingsName(Type testSettingsProviderType)
        {
            string settingName = string.Empty;

            object[] attributes = testSettingsProviderType.GetTypeInfo().GetCustomAttributes(typeof(SettingsNameAttribute), false).ToArray();
            if (attributes != null && attributes.Length > 0)
            {
                SettingsNameAttribute settingsNameAttribute = (SettingsNameAttribute)attributes[0];

                if (!string.IsNullOrEmpty(settingsNameAttribute.SettingsName))
                {
                    settingName = settingsNameAttribute.SettingsName;
                }
            }

            return settingName;
        }
    }
}