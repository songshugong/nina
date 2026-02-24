#region "copyright"

/*
    Copyright (c) 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Newtonsoft.Json;
using NINA.Core.Utility;
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace NINA.ViewModel.AI {

    public class AiAuditLogWriter : IAiAuditLogWriter {
        private static readonly object fileLock = new object();
        private readonly string logDirectory;
        private readonly string logFilePath;

        public AiAuditLogWriter() {
            logDirectory = Path.Combine(CoreUtil.APPLICATIONTEMPPATH, "Logs");
            logFilePath = Path.Combine(logDirectory, "ai-assistant-audit.jsonl");
        }

        public Task WriteAsync(AiAuditRecord record) {
            if (record == null) {
                return Task.CompletedTask;
            }

            return Task.Run(() => {
                try {
                    if (!Directory.Exists(logDirectory)) {
                        Directory.CreateDirectory(logDirectory);
                    }

                    var jsonLine = JsonConvert.SerializeObject(record);
                    lock (fileLock) {
                        File.AppendAllText(logFilePath, jsonLine + Environment.NewLine, Encoding.UTF8);
                    }
                } catch (Exception ex) {
                    Logger.Error("Failed to write AI audit log", ex);
                }
            });
        }
    }
}
