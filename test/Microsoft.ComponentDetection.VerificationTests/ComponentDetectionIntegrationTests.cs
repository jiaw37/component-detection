﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.ComponentDetection.Contracts.BcdeModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;

namespace Microsoft.ComponentDetection.VerificationTests
{
    [TestClass]
    public class ComponentDetectionIntegrationTests 
    {
        private string oldLogFileContents;
        private string newLogFileContents;
        private DefaultGraphScanResult oldScanResult;
        private DefaultGraphScanResult newScanResult;
        private List<string> bumpedDetectorVersions;
        private double allowedTimeDriftRatio;

        [TestInitialize]
        public void GatherResources()
        {
            var oldGithubArtifactsDir = Environment.GetEnvironmentVariable("GITHUB_OLD_ARTIFACTS_DIR");
            var newGithubArtifactsDir = Environment.GetEnvironmentVariable("GITHUB_NEW_ARTIFACTS_DIR");
            var allowedTimeDriftRatioString = Environment.GetEnvironmentVariable("ALLOWED_TIME_DRIFT_RATIO");
            allowedTimeDriftRatio = string.IsNullOrEmpty(allowedTimeDriftRatioString) ? .1 : double.Parse(allowedTimeDriftRatioString);

            SetupGithub(oldGithubArtifactsDir, newGithubArtifactsDir);
        }

        [TestMethod]
        public void LogFileHasNoErrors()
        {
            // make sure the new log does not contain any error messages.
            int errorIndex = newLogFileContents.IndexOf("[ERROR]");
            if (errorIndex >= 0)
            {
                // prints out the line that the error occured.
                string errorMessage = $"An Error was found: {newLogFileContents.Substring(errorIndex, 200)}";
                throw new Exception(errorMessage);
            }
        }

        [TestMethod]
        public void CheckManifestFiles_ExcludingExperimentalDetectors()
        {
            // can't just compare contents since the order of detectors is non deterministic.
            // Parse out array of components
            // make sure each component id has identical fields.
            // if any are lost, error, new ones should come with a bumped detector version, which is checked during the detectors counts test.
            var experimentalDetectorsId = GetExperimentalDetectorsId(newScanResult.DetectorsInScan, oldScanResult.DetectorsInScan);

            var newComponents = newScanResult.ComponentsFound.Where(c => !experimentalDetectorsId.Contains(c.DetectorId));
            var oldComponents = oldScanResult.ComponentsFound.Where(c => !experimentalDetectorsId.Contains(c.DetectorId));

            Dictionary<string, ScannedComponent> newComponentDictionary = GetComponentDictionary(newComponents);
            Dictionary<string, ScannedComponent> oldComponentDictionary = GetComponentDictionary(oldComponents);
            using (new AssertionScope())
            {
                CompareDetectedComponents(oldComponents, newComponentDictionary, "new");
                CompareDetectedComponents(newComponents, oldComponentDictionary, "old");
                DependencyGraphCollection oldGraphs = oldScanResult.DependencyGraphs;
                DependencyGraphCollection newGraphs = newScanResult.DependencyGraphs;
                CompareGraphs(oldGraphs, newGraphs, "old", "new");
                CompareGraphs(newGraphs, oldGraphs, "new", "old");
            }
        }

        private ISet<string> GetExperimentalDetectorsId(IEnumerable<Detector> newScanDetectors, IEnumerable<Detector> oldScanDetectors)
        {
            var experimentalDetectorsId = new HashSet<string>();

            foreach (var detector in newScanDetectors)
            {
                if (detector.IsExperimental)
                {
                    experimentalDetectorsId.Add(detector.DetectorId);
                }
            }

            foreach (var detector in oldScanDetectors)
            {
                if (detector.IsExperimental)
                {
                    experimentalDetectorsId.Add(detector.DetectorId);
                }
            }

            return experimentalDetectorsId;
        }

        private void CompareDetectedComponents(IEnumerable<ScannedComponent> leftComponents, Dictionary<string, ScannedComponent> rightComponentDictionary, string rightFileName)
        {
            foreach (var leftComponent in leftComponents)
            {
                var foundComponent = rightComponentDictionary.TryGetValue(GetKey(leftComponent), out var rightComponent);
                if (!foundComponent)
                {
                    foundComponent.Should().BeTrue($"The component for {GetKey(leftComponent)} was not present in the {rightFileName} manifest file. Verify this is expected behavior before proceeding");
                }

                if (leftComponent.IsDevelopmentDependency != null)
                {
                    leftComponent.IsDevelopmentDependency.Should().Be(rightComponent.IsDevelopmentDependency, $"Component: {GetKey(rightComponent)} has a different \"DevelopmentDependency\".");
                }
            }
        }

        private void CompareGraphs(DependencyGraphCollection leftGraphs, DependencyGraphCollection newGraphs, string leftGraphName, string rightGraphName)
        {
            foreach (var leftGraph in leftGraphs)
            {
                newGraphs.TryGetValue(leftGraph.Key, out var rightGraph).Should().BeTrue($"File {leftGraph.Key} is in the {leftGraphName} dependency graph, but not in the {rightGraphName} one.");

                if (rightGraph == null)
                {
                    // the rest of test depends on rightDependencies, if it is null a 
                    // NullReferenceException is going to be thrown stopping the verification process
                    // the previous test that validate its existance is going to include a meaningfull message
                    // in the test summary
                    continue;
                }

                foreach (var leftComponent in leftGraph.Value.ExplicitlyReferencedComponentIds)
                {
                    rightGraph.ExplicitlyReferencedComponentIds.Should().Contain(leftComponent, $"Component {leftComponent} was explicitly referenced in the {leftGraphName} dependency graph, but is not in the {rightGraphName} one.");
                }

                foreach (var leftComponent in leftGraph.Value.Graph)
                {
                    rightGraph.Graph.TryGetValue(leftComponent.Key, out var rightDependencies).Should().BeTrue($"Component {leftComponent} was in the {leftGraphName} dependency graph, but is not in the {rightGraphName} one.");

                    if (rightDependencies == null)
                    {
                        // the rest of test depends on rightDependencies, if it is null a 
                        // NullReferenceException is going to be thrown stopping the verification process
                        continue;
                    }

                    var leftDependenciesGraph = leftGraph.Value.Graph[leftComponent.Key];
                    if (leftDependenciesGraph != null)
                    {
                        var leftDependencies = leftGraph.Value.Graph[leftComponent.Key];
                        foreach (var leftDependency in leftDependencies)
                        {
                            rightDependencies.Should().Contain(leftDependency, $"Component dependency {leftDependency} for component {leftComponent} was not in the {rightGraphName} dependency graph.");
                        }
                        
                        leftDependencies.Should().BeEquivalentTo(rightDependencies, $"{rightGraphName} has the following components that were not found in {leftGraphName}, please verify this is expected behavior. {JsonConvert.SerializeObject(rightDependencies.Except(leftDependencies))}");
                    }
                }
            }
        }

        private Dictionary<string, ScannedComponent> GetComponentDictionary(IEnumerable<ScannedComponent> scannedComponents)
        {
            // The Maven detector currently returns duplicate components in some cases, so we do this to insulate.
            var grouping = scannedComponents.GroupBy(x => GetKey(x));
            return grouping.ToDictionary(x => x.Key, x => x.First());
        }

        private string GetKey(ScannedComponent component)
        {
            return $"{component.DetectorId}-{component.Component.Id}";
        }

        [TestMethod]
        public void CheckDetectorsRunTimesAndCounts()
        {
            // makes sure that all detectors have the same number of components found.
            // if some are lost, error. 
            // if some are new, check if version of detector is updated. if it isn't error 
            // Run times should be fairly close to identical. errors if there is an increase of more than 5%
            using (new AssertionScope())
            {
                ProcessDetectorVersions();
                string regexPattern = @"Detection time: (\w+\.\w+) seconds. |(\w+ *[\w()]+) *\|(\w+\.*\w*) seconds *\|(\d+)";
                var oldMatches = Regex.Matches(oldLogFileContents, regexPattern);
                var newMatches = Regex.Matches(newLogFileContents, regexPattern);

                newMatches.Should().HaveCountGreaterOrEqualTo(oldMatches.Count, "A detector was lost, make sure this was intentional.");

                var detectorTimes = new Dictionary<string, float>();
                var detectorCounts = new Dictionary<string, int>();
                foreach (Match match in oldMatches)
                {
                    if (!match.Groups[2].Success)
                    {
                        detectorTimes.Add("TotalTime", float.Parse(match.Groups[1].Value));
                    }
                    else
                    {
                        string detectorId = match.Groups[2].Value;
                        detectorTimes.Add(detectorId, float.Parse(match.Groups[3].Value));
                        detectorCounts.Add(detectorId, int.Parse(match.Groups[4].Value));
                    }
                }

                // fail at the end to gather all failures instead of just the first.
                foreach (Match match in newMatches)
                {
                    // for each detector and overall, make sure the time doesn't increase by more than 10%
                    // for each detector make sure component counts do not change. if they increase, make sure the version of the detector was bumped.
                    if (!match.Groups[2].Success)
                    {
                        detectorTimes.TryGetValue("TotalTime", out var oldTime);
                        float newTime = float.Parse(match.Groups[1].Value);

                        float maxTimeThreshold = (float)(oldTime + Math.Max(5, oldTime * allowedTimeDriftRatio));
                        newTime.Should().BeLessOrEqualTo(maxTimeThreshold, $"Total Time take increased by a large amount. Please verify before continuing. old time: {oldTime}, new time: {newTime}");
                    }
                    else
                    {
                        string detectorId = match.Groups[2].Value;
                        int newCount = int.Parse(match.Groups[4].Value);
                        if (detectorCounts.TryGetValue(detectorId, out var oldCount))
                        {
                            newCount.Should().BeGreaterOrEqualTo(oldCount, $"{oldCount - newCount} Components were lost for detector {detectorId}. Verify this is expected behavior. \n Old Count: {oldCount}, PPE Count: {newCount}");

                            (newCount > oldCount && !bumpedDetectorVersions.Contains(detectorId)).Should().BeFalse($"{newCount - oldCount} New Components were found for detector {detectorId}, but the detector version was not updated.");
                        }
                    }
                }
            }
        }

        private void ProcessDetectorVersions()
        {
            var oldDetectors = oldScanResult.DetectorsInScan;
            var newDetectors = newScanResult.DetectorsInScan;
            bumpedDetectorVersions = new List<string>();
            foreach (var cd in oldDetectors)
            {
                var newDetector = newDetectors.FirstOrDefault(det => det.DetectorId == cd.DetectorId);

                if (newDetector == null)
                {
                    newDetector.Should().NotBeNull($"the detector {cd.DetectorId} was lost, verify this is expected behavior");
                    continue;
                }

                newDetector.Version.Should().BeGreaterOrEqualTo(cd.Version, $"the version for detector {cd.DetectorId} was unexpectedly reduced. please check all detector versions and verify this behavior.");

                if (newDetector.Version > cd.Version)
                {
                    bumpedDetectorVersions.Add(cd.DetectorId);
                }

                cd.SupportedComponentTypes.Should().OnlyContain(type => newDetector.SupportedComponentTypes.Contains(type), "the detector {cd.DetectorId} has lost suppported component types. Verify this is expected behavior.");
            }
        }

        private void SetupGithub(string oldGithubArtifactsDir, string newGithubArtifactsDir)
        {
            var oldGithubDirectory = new DirectoryInfo(oldGithubArtifactsDir);
            oldLogFileContents = GetFileTextWithPattern("GovCompDisc_Log*.log", oldGithubDirectory);
            oldScanResult = JsonConvert.DeserializeObject<DefaultGraphScanResult>(GetFileTextWithPattern("ScanManifest*.json", oldGithubDirectory));

            var newGithubDirectory = new DirectoryInfo(newGithubArtifactsDir);
            newLogFileContents = GetFileTextWithPattern("GovCompDisc_Log*.log", newGithubDirectory);
            newScanResult = JsonConvert.DeserializeObject<DefaultGraphScanResult>(GetFileTextWithPattern("ScanManifest*.json", newGithubDirectory));
        }

        private string GetFileTextWithPattern(string pattern, DirectoryInfo directory)
        {
            return directory.GetFiles(pattern).Single().OpenText().ReadToEnd();
        }
    }
}
