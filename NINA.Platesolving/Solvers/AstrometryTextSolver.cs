#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NINA.Core.Enum;
using NINA.Core.Locale;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Core.Utility.Http;
using NINA.Core.Utility.Notification;
using NINA.Image.ImageAnalysis;
using NINA.Image.Interfaces;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.PlateSolving.Solvers {

    internal class AstrometryTextSolver : BaseSolver {
        private const string AUTHURL = "/api/login/";
        private const string UPLOADURL = "/api/upload";
        private const string SUBMISSIONURL = "/api/submissions/{0}";
        private const string JOBSTATUSURL = "/api/jobs/{0}";
        private const string JOBCALIBRATIONURL = "/api/jobs/{0}/calibration/";

        private const int MINIMUM_STAR_COUNT = 5;
        private const int MAXIMUM_UPLOADED_STARS = 200;
        private const int MAX_SUBMISSION_POLL_ATTEMPTS = 600;
        private const int MAX_JOB_POLL_ATTEMPTS = 600;
        private const int MAX_INVALID_RESPONSE_COUNT = 5;
        private static readonly TimeSpan POLL_DELAY = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan SOLVER_TIMEOUT = TimeSpan.FromMinutes(10);

        private readonly string _apiurl;
        private readonly string _apikey;

        [Serializable()]
        private class AstrometryNetFailedException : Exception {

            internal AstrometryNetFailedException(string type, JObject response) : base(CreateErrorMessage(type, response)) {
            }

            private static string CreateErrorMessage(string type, JObject response) {
                string message;
                string status = response?.GetValue("status")?.ToString() ?? "unknown";

                if (status == "failure") {
                    message = $"{type} failed to solve";
                    Logger.Info($"Plate Solving: Astrometry.net: {message}");
                } else if (status == "error") {
                    message = response?.GetValue("errormessage")?.ToString() ?? "Unknown error";
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

        private static JObject SafeParseJson(string response, string context) {
            if (string.IsNullOrWhiteSpace(response)) {
                Logger.Warning($"Plate Solving: Astrometry.net ({context}): Empty response received.");
                return null;
            }

            try {
                return JObject.Parse(response);
            } catch (JsonException ex) {
                Logger.Warning($"Plate Solving: Astrometry.net ({context}): Failed to parse JSON response. {ex.Message}");
                return null;
            }
        }

        private async Task<JObject> Authenticate(CancellationToken cancellationToken) {
            string requestJson = "{\"apikey\":\"" + _apikey + "\"}";
            requestJson = HttpRequest.EncodeUrl(requestJson);
            string body = "request-json=" + requestJson;

            var request = new HttpPostRequest(_apiurl + AUTHURL, body, "application/x-www-form-urlencoded");
            var response = await request.Request(cancellationToken).ConfigureAwait(false);
            var authentication = SafeParseJson(response, "authenticate");
            if (authentication == null) {
                throw new InvalidOperationException(string.Format(
                    CultureInfo.InvariantCulture,
                    Loc.Instance["LblAstrometryNetInvalidResponse"],
                    "authentication"));
            }

            return authentication;
        }

        private async Task<string> GetAuthenticationToken(CancellationToken cancellationToken) {
            JObject authentication = await Authenticate(cancellationToken).ConfigureAwait(false);
            var status = authentication.GetValue("status");
            if (status?.ToString() == "success") {
                var session = authentication.GetValue("session")?.ToString();
                if (!string.IsNullOrWhiteSpace(session)) {
                    return session;
                }

                throw new InvalidOperationException(string.Format(
                    CultureInfo.InvariantCulture,
                    Loc.Instance["LblAstrometryNetInvalidResponse"],
                    "authentication"));
            }

            throw new AstrometryNetFailedException("Authentication", authentication);
        }

        private async Task<JObject> SubmitStarList(string session, byte[] data, IImageData source, CancellationToken cancellationToken) {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(CoreUtil.UserAgent);

            var requestJson = new JObject {
                ["publicly_visible"] = "n",
                ["allow_modifications"] = "d",
                ["session"] = session,
                ["allow_commercial_use"] = "d"
            };

            if (TryGetImageScaleBounds(source, out var lowerScale, out var upperScale)) {
                requestJson["scale_units"] = "arcsecperpix";
                requestJson["scale_lower"] = lowerScale;
                requestJson["scale_upper"] = upperScale;
            }

            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(requestJson.ToString(Formatting.None)), "request-json");

            using var fileContent = new ByteArrayContent(data);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(fileContent, "file", "upload.xy");

            using var response = await httpClient.PostAsync(_apiurl + UPLOADURL, form, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            string responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var submission = SafeParseJson(responseBody, "submit");
            if (submission == null) {
                throw new InvalidOperationException(string.Format(
                    CultureInfo.InvariantCulture,
                    Loc.Instance["LblAstrometryNetInvalidResponse"],
                    "submission"));
            }

            return submission;
        }

        private async Task<JObject> GetSubmissionStatus(string subid, CancellationToken cancellationToken) {
            var request = new HttpGetRequest(_apiurl + SUBMISSIONURL, subid);
            string response = await request.Request(cancellationToken).ConfigureAwait(false);
            return SafeParseJson(response, "submission status");
        }

        private async Task<JObject> GetJobStatus(string jobid, CancellationToken cancellationToken) {
            var request = new HttpGetRequest(_apiurl + JOBSTATUSURL, jobid);
            string response = await request.Request(cancellationToken).ConfigureAwait(false);
            return SafeParseJson(response, "job status");
        }

        private async Task<JObject> GetJobCalibration(string jobid, CancellationToken cancellationToken) {
            var request = new HttpGetRequest(_apiurl + JOBCALIBRATIONURL, jobid);
            string response = await request.Request(cancellationToken).ConfigureAwait(false);
            return SafeParseJson(response, "job calibration");
        }

        private async Task<string> SubmitImageJob(
            IProgress<ApplicationStatus> progress,
            IImageData source,
            string session,
            CancellationToken cancellationToken) {
            var stars = await GetDetectedStars(source, progress, cancellationToken).ConfigureAwait(false);
            var starListText = BuildStarList(stars);
            var starListBytes = Encoding.UTF8.GetBytes(starListText);

            progress?.Report(new ApplicationStatus() {
                Status = string.Format(
                    CultureInfo.InvariantCulture,
                    Loc.Instance["LblAstrometryNetStatusUploadingStars"],
                    stars.Count)
            });

            JObject submission = await SubmitStarList(session, starListBytes, source, cancellationToken).ConfigureAwait(false);
            string submissionStatus = submission.GetValue("status")?.ToString();
            string subid = submission.GetValue("subid")?.ToString();

            if (submissionStatus != "success") {
                throw new AstrometryNetFailedException($"Job submission {subid}", submission);
            }

            progress?.Report(new ApplicationStatus() {
                Status = string.Format(
                    CultureInfo.InvariantCulture,
                    Loc.Instance["LblAstrometryNetStatusWaitingForSubmission"],
                    subid)
            });

            return await WaitForJobIdFromSubmission(subid, cancellationToken).ConfigureAwait(false);
        }

        private async Task<string> WaitForJobIdFromSubmission(string submissionId, CancellationToken cancellationToken) {
            if (string.IsNullOrWhiteSpace(submissionId)) {
                throw new InvalidOperationException(string.Format(
                    CultureInfo.InvariantCulture,
                    Loc.Instance["LblAstrometryNetInvalidResponse"],
                    "submission"));
            }

            int invalidResponseCount = 0;

            for (int attempt = 0; attempt < MAX_SUBMISSION_POLL_ATTEMPTS; attempt++) {
                cancellationToken.ThrowIfCancellationRequested();

                JObject submissionStatus = await GetSubmissionStatus(submissionId, cancellationToken).ConfigureAwait(false);
                if (submissionStatus == null) {
                    invalidResponseCount++;
                    if (invalidResponseCount >= MAX_INVALID_RESPONSE_COUNT) {
                        throw new InvalidOperationException(string.Format(
                            CultureInfo.InvariantCulture,
                            Loc.Instance["LblAstrometryNetInvalidResponse"],
                            "submission status"));
                    }
                } else {
                    invalidResponseCount = 0;

                    var status = submissionStatus.GetValue("status")?.ToString();
                    if (status == "failure") {
                        throw new AstrometryNetFailedException($"Submission {submissionId}", submissionStatus);
                    }

                    JArray jobIds = submissionStatus.GetValue("jobs") as JArray;
                    if (jobIds != null) {
                        foreach (var jobIdToken in jobIds) {
                            string jobId = jobIdToken?.ToString();
                            if (!string.IsNullOrWhiteSpace(jobId)) {
                                return jobId;
                            }
                        }
                    }
                }

                await Task.Delay(POLL_DELAY, cancellationToken).ConfigureAwait(false);
            }

            throw new TimeoutException(string.Format(
                CultureInfo.InvariantCulture,
                Loc.Instance["LblAstrometryNetSubmissionTimeout"],
                submissionId));
        }

        private async Task<Calibration> GetJobResult(string jobId, CancellationToken cancellationToken) {
            int invalidResponseCount = 0;

            for (int attempt = 0; attempt < MAX_JOB_POLL_ATTEMPTS; attempt++) {
                cancellationToken.ThrowIfCancellationRequested();

                JObject jobStatus = await GetJobStatus(jobId, cancellationToken).ConfigureAwait(false);
                if (jobStatus == null) {
                    invalidResponseCount++;
                    if (invalidResponseCount >= MAX_INVALID_RESPONSE_COUNT) {
                        throw new InvalidOperationException(string.Format(
                            CultureInfo.InvariantCulture,
                            Loc.Instance["LblAstrometryNetInvalidResponse"],
                            "job status"));
                    }
                } else {
                    invalidResponseCount = 0;
                    string status = jobStatus.GetValue("status")?.ToString();

                    if (status == "success") {
                        JObject jobCalibration = await GetJobCalibration(jobId, cancellationToken).ConfigureAwait(false);
                        if (jobCalibration == null) {
                            throw new InvalidOperationException(string.Format(
                                CultureInfo.InvariantCulture,
                                Loc.Instance["LblAstrometryNetInvalidResponse"],
                                "job calibration"));
                        }

                        var calibration = jobCalibration.ToObject<Calibration>();
                        if (calibration == null) {
                            throw new InvalidOperationException(string.Format(
                                CultureInfo.InvariantCulture,
                                Loc.Instance["LblAstrometryNetInvalidResponse"],
                                "job calibration"));
                        }

                        return calibration;
                    }

                    if (status == "failure") {
                        throw new AstrometryNetFailedException($"Job {jobId}", jobStatus);
                    }
                }

                await Task.Delay(POLL_DELAY, cancellationToken).ConfigureAwait(false);
            }

            throw new TimeoutException(string.Format(
                CultureInfo.InvariantCulture,
                Loc.Instance["LblAstrometryNetJobTimeout"],
                jobId));
        }

        private static string BuildStarList(IReadOnlyList<DetectedStar> stars) {
            var sb = new StringBuilder();
            sb.AppendLine("# X Y");

            foreach (var star in stars) {
                sb.Append(star.Position.X.ToString("F3", CultureInfo.InvariantCulture));
                sb.Append(' ');
                sb.Append(star.Position.Y.ToString("F3", CultureInfo.InvariantCulture));
                sb.AppendLine();
            }

            return sb.ToString();
        }

        private static bool IsStarValid(DetectedStar star) {
            if (star == null) {
                return false;
            }

            try {
                return !(float.IsNaN(star.Position.X) || float.IsInfinity(star.Position.X) ||
                         float.IsNaN(star.Position.Y) || float.IsInfinity(star.Position.Y));
            } catch (NullReferenceException) {
                return false;
            }
        }

        private static bool TryGetImageScaleBounds(IImageData source, out double lowerScale, out double upperScale) {
            lowerScale = 0;
            upperScale = 0;

            try {
                var cameraMetaData = source?.MetaData?.Camera;
                var telescopeMetaData = source?.MetaData?.Telescope;

                if (cameraMetaData == null || telescopeMetaData == null) {
                    return false;
                }

                double pixelSize = cameraMetaData.PixelSize;
                double focalLength = telescopeMetaData.FocalLength;
                if (pixelSize <= 0.1d || focalLength <= 1.0d) {
                    return false;
                }

                double scale = (pixelSize / focalLength) * 206.265d;
                lowerScale = scale * 0.8d;
                upperScale = scale * 1.2d;
                return true;
            } catch (Exception ex) {
                Logger.Warning($"Plate Solving: Astrometry.net: Failed to calculate image scale bounds. {ex.Message}");
                return false;
            }
        }

        private static bool IsUsableStar(DetectedStar star) {
            if (!IsStarValid(star)) {
                return false;
            }

            return star.MaxBrightness > 0 || star.AverageBrightness > 0;
        }

        private async Task<IReadOnlyList<DetectedStar>> GetDetectedStars(IImageData source, IProgress<ApplicationStatus> progress, CancellationToken cancellationToken) {
            var starList = source.StarDetectionAnalysis?.StarList;
            if (starList == null || starList.Count == 0) {
                progress?.Report(new ApplicationStatus() {
                    Status = Loc.Instance["LblAstrometryNetStatusDetectingStars"]
                });

                var renderedImage = source.RenderImage();
                await renderedImage
                    .DetectStars(
                        annotateImage: false,
                        sensitivity: StarSensitivityEnum.High,
                        noiseReduction: NoiseReductionEnum.None,
                        cancelToken: cancellationToken,
                        progress: null)
                    .ConfigureAwait(false);

                starList = source.StarDetectionAnalysis?.StarList;
            }

            if (starList == null || starList.Count == 0) {
                throw new InvalidOperationException(Loc.Instance["LblAstrometryNetNoStarsDetected"]);
            }

            var stars = starList
                .Where(IsUsableStar)
                .OrderByDescending(s => s.MaxBrightness)
                .ThenByDescending(s => s.AverageBrightness)
                .Take(MAXIMUM_UPLOADED_STARS)
                .ToList();

            if (stars.Count < MINIMUM_STAR_COUNT) {
                throw new InvalidOperationException(string.Format(
                    CultureInfo.InvariantCulture,
                    Loc.Instance["LblAstrometryNetNotEnoughStars"],
                    MINIMUM_STAR_COUNT,
                    stars.Count));
            }

            return stars;
        }

        protected override async Task<PlateSolveResult> SolveAsyncImpl(
            IImageData source,
            PlateSolveParameter parameter,
            PlateSolveImageProperties imageProperties,
            IProgress<ApplicationStatus> progress,
            CancellationToken cancelToken) {
            PlateSolveResult result = new PlateSolveResult();

            try {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancelToken);
                cts.CancelAfter(SOLVER_TIMEOUT);
                var timeoutToken = cts.Token;

                progress?.Report(new ApplicationStatus() { Status = Loc.Instance["LblAstrometryNetStatusAuthenticating"] });
                var session = await GetAuthenticationToken(timeoutToken).ConfigureAwait(false);

                var jobId = await SubmitImageJob(progress, source, session, timeoutToken).ConfigureAwait(false);

                progress?.Report(new ApplicationStatus() {
                    Status = string.Format(
                        CultureInfo.InvariantCulture,
                        Loc.Instance["LblAstrometryNetStatusGettingResult"],
                        jobId)
                });

                Calibration jobInfo = await GetJobResult(jobId, timeoutToken).ConfigureAwait(false);

                result.Flipped = jobInfo.parity < 0;
                result.PositionAngle = 360 - (180 - jobInfo.orientation + 360);
                result.Pixscale = jobInfo.pixscale;
                result.Radius = jobInfo.radius;
                result.Coordinates = new Astrometry.Coordinates(jobInfo.ra, jobInfo.dec, Astrometry.Epoch.J2000, Astrometry.Coordinates.RAType.Degrees);
            } catch (OperationCanceledException) {
                if (!cancelToken.IsCancellationRequested) {
                    Logger.Error("Plate Solving: Astrometry.net text upload solver timed out after 10 minutes.");
                }
                result.Success = false;
            } catch (Exception ex) {
                result.Success = false;
                if (!parameter.DisableNotifications) {
                    Notification.ShowError(string.Format(Loc.Instance["LblAstrometryNetSolveFailed"], ex.Message));
                }
            } finally {
                progress?.Report(new ApplicationStatus() { Status = string.Empty });
            }

            return result;
        }

        protected override void EnsureSolverValid(PlateSolveParameter parameter) {
            if (string.IsNullOrWhiteSpace(_apikey)) {
                throw new ArgumentException("Astrometry.net API key is not configured");
            }

            if (string.IsNullOrWhiteSpace(_apiurl)) {
                throw new ArgumentException("Astrometry.net API URL is not configured");
            }

            if (Regex.IsMatch(_apikey, @"\s")) {
                throw new ArgumentException("Astrometry.net API key contains an invalid space character");
            }
        }
    }
}
