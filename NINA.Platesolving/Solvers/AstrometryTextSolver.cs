#region "copyright"
/*
    Copyright © 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors
    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.
*/
#endregion "copyright"

using Newtonsoft.Json.Linq;
using NINA.Core.Enum;
using NINA.Image.Interfaces;
using NINA.Core.Utility;
using NINA.Core.Utility.Http;
using NINA.Core.Utility.Notification;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using NINA.Core.Model;
using NINA.Image.FileFormat;
using System.Linq;

namespace NINA.PlateSolving.Solvers {

    internal class AstrometryTextSolver : BaseSolver {
        private const string AUTHURL = "/api/login/";
        private const string UPLOADURL = "/api/upload";
        private const string SUBMISSIONURL = "/api/submissions/{0}";
        private const string JOBSTATUSURL = "/api/jobs/{0}";
        private const string JOBINFOURL = "/api/jobs/{0}/info/";
        private const string JOBCALIBRATIONURL = "/api/jobs/{0}/calibration/";
        private const string ANNOTATEDIMAGEURL = "/annotated_display/{0}";

        private string _apiurl;
        private string _apikey;

        private class TextUploader : HttpRequest<string> {
            public string ContentType { get; }
            public NameValueCollection NameValueCollection { get; }
            public byte[] DataBytes { get; }
            public string ParamName { get; }

            public TextUploader(string url, byte[] data, string paramName, string contentType, NameValueCollection nvc) : base(url) {
                this.DataBytes = data;
                this.ParamName = paramName;
                this.ContentType = contentType;
                this.NameValueCollection = nvc;
            }

            public override async Task<string> Request(CancellationToken ct, IProgress<int> progress = null) {
                int maxRetries = 3;
                int currentRetry = 0;
                while (true) {
                    try {
                        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) }; 
                        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(CoreUtil.UserAgent);
                        using var form = new MultipartFormDataContent();
                        foreach (string key in NameValueCollection.Keys) {
                            form.Add(new StringContent(NameValueCollection[key]), key);
                        }
                        var fileContent = new ByteArrayContent(DataBytes);
                        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(ContentType);
                        form.Add(fileContent, ParamName, "upload.xy");

                        using var response = await httpClient.PostAsync(Url, form, ct).ConfigureAwait(false);
                        response.EnsureSuccessStatusCode();
                        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    } catch (OperationCanceledException) {
                        ct.ThrowIfCancellationRequested();
                        return string.Empty;
                    } catch (Exception ex) {
                        currentRetry++;
                        if (currentRetry > maxRetries) throw;
                        Logger.Warning($"[TextUploader] Upload failed ({ex.Message}). Retrying {currentRetry}/{maxRetries}...");
                        await Task.Delay(1000, ct);
                    }
                }
            }
        }

        [Serializable()]
        internal class AstrometryNetFailedException : Exception {
            internal AstrometryNetFailedException(string type, JObject response) : base(CreateErrorMessage(type, response)) { }
            private static string CreateErrorMessage(string type, JObject response) {
                string message;
                var statusToken = response?.GetValue("status");
                string status = statusToken != null ? statusToken.ToString() : "unknown";
                if (status == "failure") {
                    message = $"{type} failed to solve";
                    Logger.Info($"Plate Solving: Astrometry.net: {message}");
                } else if (status == "error") {
                    var errorMsgToken = response?.GetValue("errormessage");
                    message = errorMsgToken != null ? errorMsgToken.ToString() : "Unknown error";
                    Logger.Error($"Plate Solving: Astrometry.net: {type} failed. Server response: {message}");
                } else {
                    message = "Unspecified error";
                    Logger.Error($"Plate Solving: Astrometry.net: {type} failed. {message}");
                }
                return message;
            }
        }

        public AstrometryTextSolver(string apiurl, string apikey) {
            this._apiurl = apiurl;
            this._apikey = apikey;
        }

        private JObject SafeParseJson(string response, string context) {
            if (string.IsNullOrWhiteSpace(response)) return null;
            try {
                return JObject.Parse(response);
            } catch (Exception ex) {
                Logger.Warning($"[{context}] JSON Parse Failed: {ex.Message}");
                return null;
            }
        }

        private Task<(string Content, int Count)> ExtractStarList(IImageData source, CancellationToken token) {
            return Task.Run(() => {
                int width = source.Properties.Width;
                int height = source.Properties.Height;
                ushort[] pixels = source.Data.FlatArray;

                long sum = 0, sqSum = 0;
                int step = 50, count = 0;
                for (int i = 0; i < pixels.Length; i += step) {
                    int val = pixels[i];
                    sum += val;
                    sqSum += (long)val * val;
                    count++;
                }
                double mean = sum / (double)count;
                double variance = (sqSum / (double)count) - (mean * mean);
                double stdDev = Math.Sqrt(variance);

                var starList = new List<Tuple<double, double, double>>();
                var strategies = new[] {
                    new { Sigma = 6.0, MinPixels = 4 },
                    new { Sigma = 4.0, MinPixels = 2 },
                    new { Sigma = 2.0, MinPixels = 1 }
                };

                foreach (var strat in strategies) {
                    starList.Clear();
                    double threshold = mean + (strat.Sigma * stdDev);
                    if (threshold < 100) threshold = 100;

                    for (int y = 2; y < height - 2; y += 2) {
                        for (int x = 2; x < width - 2; x += 2) {
                            int idx = y * width + x;
                            ushort val = pixels[idx];
                            if (val < threshold) continue;

                            if (val >= pixels[idx - 1] && val >= pixels[idx + 1] &&
                                val >= pixels[idx - width] && val >= pixels[idx + width]) {

                                double mX = 0, mY = 0, mass = 0;
                                int pixelCount = 0;
                                for (int wy = -1; wy <= 1; wy++) {
                                    for (int wx = -1; wx <= 1; wx++) {
                                        int pIdx = (y + wy) * width + (x + wx);
                                        double pVal = pixels[pIdx];
                                        double flux = pVal - mean;
                                        if (flux < 0) flux = 0;
                                        mX += (x + wx) * flux;
                                        mY += (y + wy) * flux;
                                        mass += flux;
                                        if (pVal > threshold * 0.9) pixelCount++;
                                    }
                                }
                                if (pixelCount < strat.MinPixels || mass <= 0) continue;
                                starList.Add(new Tuple<double, double, double>(mX / mass, mY / mass, mass));
                            }
                        }
                    }
                    if (starList.Count >= 20) break;
                }

                var topStars = starList.OrderByDescending(s => s.Item3).Take(50).ToList();

                if (topStars.Count < 5) {
                    string msg = $"Extraction failed: Found only {topStars.Count} stars (Minimum 5 required).";
                    Logger.Warning($"[TextSolver] {msg}");
                    throw new Exception(msg); 
                }

                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                sb.AppendLine("# X  Y");
                foreach (var star in topStars) {
                    sb.AppendLine($"{star.Item1:F3} {star.Item2:F3}");
                }
                return (sb.ToString(), topStars.Count);
            }, token);
        }

        private async Task<JObject> SubmitImageBytes(byte[] data, string session, IImageData source, CancellationToken canceltoken) {
            string scaleJsonPart = "";
            try {
                if (source?.MetaData?.Camera != null && source?.MetaData?.Telescope != null) {
                    double pixelSize = source.MetaData.Camera.PixelSize;
                    double focalLength = source.MetaData.Telescope.FocalLength;
                    if (pixelSize > 0.1 && focalLength > 1.0) {
                        double scale = (pixelSize / focalLength) * 206.265;
                        scaleJsonPart = $", \"scale_units\": \"arcsecperpix\", \"scale_lower\": {(scale * 0.8).ToString(System.Globalization.CultureInfo.InvariantCulture)}, \"scale_upper\": {(scale * 1.2).ToString(System.Globalization.CultureInfo.InvariantCulture)}";
                    }
                }
            } catch { }

            NameValueCollection nvc = new NameValueCollection();
            string jsonConfig = "{\"publicly_visible\": \"n\", \"allow_modifications\": \"d\", \"session\": \"" + session + "\", \"allow_commercial_use\": \"d\"" + scaleJsonPart + "}";
            nvc.Add("request-json", jsonConfig);

            var request = new TextUploader(_apiurl + UPLOADURL, data, "file", "application/octet-stream", nvc);
            string response = await request.Request(canceltoken).ConfigureAwait(false);
            var json = SafeParseJson(response, "SubmitImageBytes");
            if (json == null) throw new Exception("Upload failed: Empty response.");
            return json;
        }

        private async Task<JObject> SubmitImage(string session, IImageData source, CancellationToken cancelToken, IProgress<ApplicationStatus> progress) {

            progress?.Report(new ApplicationStatus() { Status = "[1/3] Extracting star points locally..." });
            Logger.Info("[TextSolver] Extracting stars...");
 
           
            var result = await ExtractStarList(source, cancelToken).ConfigureAwait(false);
            string starListText = result.Content;
            int starCount = result.Count;

            byte[] data = System.Text.Encoding.UTF8.GetBytes(starListText);

            
            string statusMsg = $"[2/3] extract {starCount} stars，uploading ({data.Length} bytes)...";

            progress?.Report(new ApplicationStatus() { Status = statusMsg });
            Logger.Info($"[TextSolver] {statusMsg}");

            return await SubmitImageBytes(data, session, source, cancelToken).ConfigureAwait(false);
        }

        
        private async Task<string> SubmitImageJob(IProgress<ApplicationStatus> progress, IImageData source, string session, CancellationToken cancelToken) {
            JObject imageSubmission = await SubmitImage(session, source, cancelToken, progress).ConfigureAwait(false);
            string status = imageSubmission?.GetValue("status")?.ToString();
            string subid = imageSubmission?.GetValue("subid")?.ToString() ?? "";
            if (status != "success") throw new AstrometryNetFailedException($"Job submission {subid}", imageSubmission);
            progress?.Report(new ApplicationStatus() { Status = $"[3/3] Upload successful.queuing up.... (ID: {subid})" });
            while (true) {
                cancelToken.ThrowIfCancellationRequested();
                JObject subStatus = await GetSubmissionStatus(subid, cancelToken).ConfigureAwait(false);
                if (subStatus != null) {
                    JArray jobs = (JArray)subStatus.GetValue("jobs");
                    if (jobs != null && jobs.Count > 0 && jobs.First.ToString().Length > 0) return jobs.First.ToString();
                }
                await Task.Delay(1000);
            }
        }
        private async Task<JObject> Authenticate(CancellationToken t) {
            string json = "{\"apikey\":\"" + _apikey + "\"}";
            json = HttpRequest.EncodeUrl(json);
            string body = "request-json=" + json;
            var request = new HttpPostRequest(_apiurl + AUTHURL, body, "application/x-www-form-urlencoded");
            var response = await request.Request(t).ConfigureAwait(false);
            return JObject.Parse(response);
        }
        private async Task<string> GetAuthenticationToken(CancellationToken t) {
            JObject authentication = await Authenticate(t).ConfigureAwait(false);
            var status = authentication.GetValue("status");
            if (status?.ToString() == "success") return authentication.GetValue("session").ToString();
            else throw new AstrometryNetFailedException("Authentication", authentication);
        }
        private async Task<JObject> GetSubmissionStatus(string s, CancellationToken t) {
            var request = new HttpGetRequest(_apiurl + SUBMISSIONURL, s);
            string response = await request.Request(t).ConfigureAwait(false);
            return SafeParseJson(response, "SubStatus");
        }
        private async Task<JObject> GetJobStatus(string j, CancellationToken t) {
            var request = new HttpGetRequest(_apiurl + JOBSTATUSURL, j);
            string response = await request.Request(t).ConfigureAwait(false);
            return SafeParseJson(response, "JobStatus");
        }
        private async Task<JObject> GetJobCalibration(string j, CancellationToken t) {
            var request = new HttpGetRequest(_apiurl + JOBCALIBRATIONURL, j);
            string response = await request.Request(t).ConfigureAwait(false);
            return SafeParseJson(response, "JobCal");
        }
        private async Task<Calibration> GetJobResult(string j, CancellationToken t) {
            while (true) {
                t.ThrowIfCancellationRequested();
                JObject s = await GetJobStatus(j, t).ConfigureAwait(false);
                if (s != null) {
                    string st = s.GetValue("status")?.ToString();
                    if (st == "success") break;
                    else if (st == "failure") throw new AstrometryNetFailedException($"Job {j}", s);
                }
                await Task.Delay(1000);
            }
            JObject job = await GetJobCalibration(j, t).ConfigureAwait(false);
            if (job == null) throw new Exception("Calib null");
            return job.ToObject<Calibration>();
        }
        protected override async Task<PlateSolveResult> SolveAsyncImpl(IImageData source, PlateSolveParameter parameter, PlateSolveImageProperties imageProperties, IProgress<ApplicationStatus> progress, CancellationToken cancelToken) {
            PlateSolveResult result = new PlateSolveResult();
            try {
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancelToken)) {
                    cts.CancelAfter(TimeSpan.FromMinutes(5)); 
                    progress.Report(new ApplicationStatus() { Status = "Logging in..." });
                    var session = await GetAuthenticationToken(cancelToken);
                    var jobId = await SubmitImageJob(progress, source, session, cancelToken);
                    progress.Report(new ApplicationStatus() { Status = $"calculating (Job {jobId})..." });
                    Calibration jobinfo = await GetJobResult(jobId, cancelToken);
                    result.Flipped = jobinfo.parity < 0;
                    result.PositionAngle = (jobinfo.orientation - 180.0 + 360.0) % 360.0;
                    result.Pixscale = jobinfo.pixscale;
                    result.Coordinates = new Astrometry.Coordinates(jobinfo.ra, jobinfo.dec, Astrometry.Epoch.J2000, Astrometry.Coordinates.RAType.Degrees);
                    result.Success = true;
                }
            } catch (Exception ex) {
                result.Success = false;
                if (!parameter.DisableNotifications) Notification.ShowError($"Error: {ex.Message}");
            }
            if (result.Success) progress.Report(new ApplicationStatus() { Status = "✅solve success！" });
            else progress.Report(new ApplicationStatus() { Status = "❌solve fail" });
            return result;
        }
        protected override void EnsureSolverValid(PlateSolveParameter parameter) {
            if (string.IsNullOrWhiteSpace(_apikey)) throw new ArgumentException("API key missing");
        }
    }
}