﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.RemoveUnnecessarySuppressions;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Diagnostics.CSharp;
using Microsoft.CodeAnalysis.Diagnostics.EngineV2;
using Microsoft.CodeAnalysis.Editor.Test;
using Microsoft.CodeAnalysis.Editor.UnitTests.Utilities;
using Microsoft.CodeAnalysis.Editor.UnitTests.Workspaces;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Shared.TestHooks;
using Microsoft.CodeAnalysis.SolutionCrawler;
using Microsoft.CodeAnalysis.Test.Utilities;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.UnitTests;
using Microsoft.VisualStudio.LanguageServices;
using Roslyn.Test.Utilities;
using Roslyn.Utilities;
using Xunit;
using static Microsoft.CodeAnalysis.CommonDiagnosticAnalyzers;

namespace Microsoft.CodeAnalysis.Editor.UnitTests.Diagnostics
{
    [UseExportProvider]
    public class DiagnosticAnalyzerServiceTests
    {
        [Fact]
        public async Task TestHasSuccessfullyLoadedBeingFalse()
        {
            var workspace = new AdhocWorkspace();

            var analyzerReference = new AnalyzerImageReference(ImmutableArray.Create<DiagnosticAnalyzer>(new Analyzer()));
            workspace.TryApplyChanges(workspace.CurrentSolution.WithAnalyzerReferences(new[] { analyzerReference }));

            var document = GetDocumentFromIncompleteProject(workspace);

            // create listener/service/analyzer
            var listener = new AsynchronousOperationListener();
            var service = new MyDiagnosticAnalyzerService(listener);
            var analyzer = service.CreateIncrementalAnalyzer(workspace);

            // listen to events
            // check empty since this could be called to clear up existing diagnostics
            service.DiagnosticsUpdated += (s, a) => Assert.Empty(a.Diagnostics);

            // now call each analyze method. none of them should run.
            await RunAllAnalysisAsync(analyzer, document).ConfigureAwait(false);

            // wait for all events to raised
            await listener.ExpeditedWaitAsync().ConfigureAwait(false);
        }

        [Fact]
        public async Task TestHasSuccessfullyLoadedBeingFalseFSAOn()
        {
            var workspace = new AdhocWorkspace();

            var analyzerReference = new AnalyzerImageReference(ImmutableArray.Create<DiagnosticAnalyzer>(new Analyzer()));

            var options = workspace.CurrentSolution.Options
                .WithChangedOption(SolutionCrawlerOptions.BackgroundAnalysisScopeOption, LanguageNames.CSharp, BackgroundAnalysisScope.FullSolution);

            workspace.TryApplyChanges(workspace.CurrentSolution.WithOptions(options).WithAnalyzerReferences(new[] { analyzerReference }));
            var document = GetDocumentFromIncompleteProject(workspace);

            // open document
            workspace.OpenDocument(document.Id);

            await TestAnalyzerAsync(workspace, document, AnalyzerResultSetter, expectedSyntax: true, expectedSemantic: true);
        }

        [Fact]
        public async Task TestHasSuccessfullyLoadedBeingFalseWhenFileOpened()
        {
            var workspace = new AdhocWorkspace();

            var analyzerReference = new AnalyzerImageReference(ImmutableArray.Create<DiagnosticAnalyzer>(new Analyzer()));
            workspace.TryApplyChanges(workspace.CurrentSolution.WithAnalyzerReferences(new[] { analyzerReference }));

            var document = GetDocumentFromIncompleteProject(workspace);

            // open document
            workspace.OpenDocument(document.Id);

            await TestAnalyzerAsync(workspace, document, AnalyzerResultSetter, expectedSyntax: true, expectedSemantic: true);
        }

        [Fact]
        public async Task TestHasSuccessfullyLoadedBeingFalseWhenFileOpenedWithCompilerAnalyzer()
        {
            var workspace = new AdhocWorkspace();

            var analyzerReference = new AnalyzerImageReference(ImmutableArray.Create<DiagnosticAnalyzer>(new CSharpCompilerDiagnosticAnalyzer()));
            workspace.TryApplyChanges(workspace.CurrentSolution.WithAnalyzerReferences(new[] { analyzerReference }));

            var document = GetDocumentFromIncompleteProject(workspace);

            // open document
            workspace.OpenDocument(document.Id);

            await TestAnalyzerAsync(workspace, document, CompilerAnalyzerResultSetter, expectedSyntax: true, expectedSemantic: false);
        }

        [Fact]
        public async Task TestHasSuccessfullyLoadedBeingFalseWithCompilerAnalyzerFSAOn()
        {
            var workspace = new AdhocWorkspace();

            var analyzerReference = new AnalyzerImageReference(ImmutableArray.Create<DiagnosticAnalyzer>(new CSharpCompilerDiagnosticAnalyzer()));

            var options = workspace.CurrentSolution.Options
                .WithChangedOption(SolutionCrawlerOptions.BackgroundAnalysisScopeOption, LanguageNames.CSharp, BackgroundAnalysisScope.FullSolution);

            workspace.TryApplyChanges(workspace.CurrentSolution.WithOptions(options).WithAnalyzerReferences(new[] { analyzerReference }));

            var document = GetDocumentFromIncompleteProject(workspace);

            await TestAnalyzerAsync(workspace, document, CompilerAnalyzerResultSetter, expectedSyntax: true, expectedSemantic: false);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task TestDisabledByDefaultAnalyzerEnabledWithEditorConfig(bool enabledWithEditorconfig)
        {
            using var workspace = new AdhocWorkspace();

            var analyzerReference = new AnalyzerImageReference(ImmutableArray.Create<DiagnosticAnalyzer>(new DisabledByDefaultAnalyzer()));

            var options = workspace.CurrentSolution.Options
                .WithChangedOption(SolutionCrawlerOptions.BackgroundAnalysisScopeOption, LanguageNames.CSharp, BackgroundAnalysisScope.FullSolution);

            workspace.TryApplyChanges(workspace.CurrentSolution.WithAnalyzerReferences(new[] { analyzerReference }).WithOptions(options));

            var project = workspace.AddProject(
                ProjectInfo.Create(
                    ProjectId.CreateNewId(),
                    VersionStamp.Create(),
                    "CSharpProject",
                    "CSharpProject",
                    LanguageNames.CSharp,
                    filePath: "z:\\CSharpProject.csproj"));

            if (enabledWithEditorconfig)
            {
                var editorconfigText = @$"
[*.cs]
dotnet_diagnostic.{DisabledByDefaultAnalyzer.s_syntaxRule.Id}.severity = warning
dotnet_diagnostic.{DisabledByDefaultAnalyzer.s_semanticRule.Id}.severity = warning
dotnet_diagnostic.{DisabledByDefaultAnalyzer.s_compilationRule.Id}.severity = warning";

                project = project.AddAnalyzerConfigDocument(".editorconfig", filePath: "z:\\.editorconfig", text: SourceText.From(editorconfigText)).Project;
            }

            var document = project.AddDocument("test.cs", SourceText.From("class A {}"), filePath: "z:\\test.cs");
            var applied = workspace.TryApplyChanges(document.Project.Solution);
            Assert.True(applied);

            // create listener/service/analyzer
            var listener = new AsynchronousOperationListener();
            var service = new MyDiagnosticAnalyzerService(listener);
            var analyzer = service.CreateIncrementalAnalyzer(workspace);

            // listen to events
            var syntaxDiagnostic = false;
            var semanticDiagnostic = false;
            var compilationDiagnostic = false;
            service.DiagnosticsUpdated += (s, a) =>
            {
                var diagnostic = Assert.Single(a.Diagnostics);
                Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);

                if (diagnostic.Id == DisabledByDefaultAnalyzer.s_syntaxRule.Id)
                {
                    syntaxDiagnostic = true;
                }
                else if (diagnostic.Id == DisabledByDefaultAnalyzer.s_semanticRule.Id)
                {
                    semanticDiagnostic = true;
                }
                else if (diagnostic.Id == DisabledByDefaultAnalyzer.s_compilationRule.Id)
                {
                    compilationDiagnostic = true;
                }
            };

            // open document
            workspace.OpenDocument(document.Id);
            await analyzer.DocumentOpenAsync(document, CancellationToken.None).ConfigureAwait(false);

            // run analysis
            await RunAllAnalysisAsync(analyzer, document).ConfigureAwait(false);

            // wait for all events to raised
            await listener.ExpeditedWaitAsync().ConfigureAwait(false);

            Assert.Equal(enabledWithEditorconfig, syntaxDiagnostic);
            Assert.Equal(enabledWithEditorconfig, semanticDiagnostic);
            Assert.Equal(enabledWithEditorconfig, compilationDiagnostic);
        }

        private static async Task TestAnalyzerAsync(
            AdhocWorkspace workspace,
            Document document,
            Func<bool, bool, ImmutableArray<DiagnosticData>, (bool, bool)> resultSetter,
            bool expectedSyntax, bool expectedSemantic)
        {
            // create listener/service/analyzer
            var listener = new AsynchronousOperationListener();
            var service = new MyDiagnosticAnalyzerService(listener);
            var analyzer = service.CreateIncrementalAnalyzer(workspace);

            var syntax = false;
            var semantic = false;

            // listen to events
            service.DiagnosticsUpdated += (s, a) =>
            {
                (syntax, semantic) = resultSetter(syntax, semantic, a.Diagnostics);
            };

            // now call each analyze method. none of them should run.
            await RunAllAnalysisAsync(analyzer, document).ConfigureAwait(false);

            // wait for all events to raised
            await listener.ExpeditedWaitAsync().ConfigureAwait(false);

            // two should have been called.
            Assert.Equal(expectedSyntax, syntax);
            Assert.Equal(expectedSemantic, semantic);
        }

        [Fact]
        public async Task TestOpenFileOnlyAnalyzerDiagnostics()
        {
            var workspace = new AdhocWorkspace();

            var analyzerReference = new AnalyzerImageReference(ImmutableArray.Create<DiagnosticAnalyzer>(new OpenFileOnlyAnalyzer()));
            workspace.TryApplyChanges(workspace.CurrentSolution.WithAnalyzerReferences(new[] { analyzerReference }));

            var project = workspace.AddProject(
                           ProjectInfo.Create(
                               ProjectId.CreateNewId(),
                               VersionStamp.Create(),
                               "CSharpProject",
                               "CSharpProject",
                               LanguageNames.CSharp));

            var document = workspace.AddDocument(project.Id, "Empty.cs", SourceText.From(""));

            // create listener/service/analyzer
            var listener = new AsynchronousOperationListener();
            var service = new MyDiagnosticAnalyzerService(listener);
            var analyzer = service.CreateIncrementalAnalyzer(workspace);

            // listen to events
            service.DiagnosticsUpdated += (s, a) =>
            {
                if (workspace.IsDocumentOpen(a.DocumentId))
                {
                    // check the diagnostics are reported
                    Assert.Equal(document.Id, a.DocumentId);
                    Assert.Equal(1, a.Diagnostics.Length);
                    Assert.Equal(OpenFileOnlyAnalyzer.s_syntaxRule.Id, a.Diagnostics[0].Id);
                }

                if (a.DocumentId == document.Id && !workspace.IsDocumentOpen(a.DocumentId))
                {
                    // check the diagnostics reported are cleared
                    Assert.Equal(0, a.Diagnostics.Length);
                }
            };

            // open document
            workspace.OpenDocument(document.Id);
            await analyzer.DocumentOpenAsync(document, CancellationToken.None).ConfigureAwait(false);

            // cause analysis
            await RunAllAnalysisAsync(analyzer, document).ConfigureAwait(false);

            // close document
            workspace.CloseDocument(document.Id);
            await analyzer.DocumentCloseAsync(document, CancellationToken.None).ConfigureAwait(false);

            await RunAllAnalysisAsync(analyzer, document).ConfigureAwait(false);

            // wait for all events to raised
            await listener.ExpeditedWaitAsync().ConfigureAwait(false);
        }

        [Fact]
        public async Task TestSynchronizeWithBuild()
        {
            var workspace = new AdhocWorkspace(VisualStudioMefHostServices.Create(TestExportProvider.ExportProviderWithCSharpAndVisualBasic));

            var analyzerReference = new AnalyzerImageReference(ImmutableArray.Create<DiagnosticAnalyzer>(new NoNameAnalyzer()));
            workspace.TryApplyChanges(workspace.CurrentSolution.WithAnalyzerReferences(new[] { analyzerReference }));

            var language = NoCompilationConstants.LanguageName;

            var project = workspace.AddProject(
                           ProjectInfo.Create(
                               ProjectId.CreateNewId(),
                               VersionStamp.Create(),
                               "NoNameProject",
                               "NoNameProject",
                               language));

            var filePath = "NoNameDoc.other";
            var document = workspace.AddDocument(
                DocumentInfo.Create(
                    DocumentId.CreateNewId(project.Id),
                    "Empty",
                    loader: TextLoader.From(TextAndVersion.Create(SourceText.From(""), VersionStamp.Create(), filePath)),
                    filePath: filePath));

            // create listener/service/analyzer
            var listener = new AsynchronousOperationListener();
            var service = new MyDiagnosticAnalyzerService(listener);
            var analyzer = service.CreateIncrementalAnalyzer(workspace);

            var syntax = false;

            // listen to events
            service.DiagnosticsUpdated += (s, a) =>
            {
                switch (a.Diagnostics.Length)
                {
                    case 0:
                        return;
                    case 1:
                        syntax |= a.Diagnostics[0].Id == NoNameAnalyzer.s_syntaxRule.Id;
                        return;
                    default:
                        AssertEx.Fail("shouldn't reach here");
                        return;
                }
            };

            // cause analysis
            var location = Location.Create(document.FilePath, textSpan: default, lineSpan: default);

            await service.SynchronizeWithBuildAsync(
                workspace,
                ImmutableDictionary<ProjectId, ImmutableArray<DiagnosticData>>.Empty.Add(
                    document.Project.Id,
                    ImmutableArray.Create(DiagnosticData.Create(Diagnostic.Create(NoNameAnalyzer.s_syntaxRule, location), document.Project))));

            // wait for all events to raised
            await listener.ExpeditedWaitAsync().ConfigureAwait(false);

            // two should have been called.
            Assert.True(syntax);

            // we should reach here without crashing
        }

        [Fact]
        public void TestHostAnalyzerOrdering()
        {
            var workspace = new AdhocWorkspace(VisualStudioMefHostServices.Create(TestExportProvider.ExportProviderWithCSharpAndVisualBasic));

            var analyzerReference = new AnalyzerImageReference(ImmutableArray.Create<DiagnosticAnalyzer>(
                new Priority20Analyzer(),
                new Priority15Analyzer(),
                new Priority10Analyzer(),
                new Priority1Analyzer(),
                new Priority0Analyzer(),
                new CSharpCompilerDiagnosticAnalyzer(),
                new Analyzer()
            ));

            workspace.TryApplyChanges(workspace.CurrentSolution.WithAnalyzerReferences(new[] { analyzerReference }));

            var project = workspace.AddProject(
                          ProjectInfo.Create(
                              ProjectId.CreateNewId(),
                              VersionStamp.Create(),
                              "Dummy",
                              "Dummy",
                              LanguageNames.CSharp));

            // create listener/service/analyzer
            var listener = new AsynchronousOperationListener();
            var service = new MyDiagnosticAnalyzerService(listener);

            var incrementalAnalyzer = (DiagnosticIncrementalAnalyzer)service.CreateIncrementalAnalyzer(workspace);
            var analyzers = incrementalAnalyzer.GetAnalyzersTestOnly(project).ToArray();

            AssertEx.Equal(new[]
            {
                typeof(FileContentLoadAnalyzer),
                typeof(CSharpCompilerDiagnosticAnalyzer),
                typeof(Analyzer),
                typeof(Priority0Analyzer),
                typeof(Priority1Analyzer),
                typeof(Priority10Analyzer),
                typeof(Priority15Analyzer),
                typeof(Priority20Analyzer)
            }, analyzers.Select(a => a.GetType()));
        }

        [Fact]
        public async Task TestHostAnalyzerErrorNotLeaking()
        {
            var workspace = new AdhocWorkspace();

            var solution = workspace.CurrentSolution;

            var analyzerReference = new AnalyzerImageReference(ImmutableArray.Create<DiagnosticAnalyzer>(
                new LeakDocumentAnalyzer(), new LeakProjectAnalyzer()));

            var options = solution.Options.WithChangedOption(SolutionCrawlerOptions.BackgroundAnalysisScopeOption, LanguageNames.CSharp, BackgroundAnalysisScope.FullSolution);
            workspace.TryApplyChanges(solution.WithOptions(options).WithAnalyzerReferences(new[] { analyzerReference }));

            var projectId = ProjectId.CreateNewId();
            var project = workspace.AddProject(
                          ProjectInfo.Create(
                              projectId,
                              VersionStamp.Create(),
                              "Dummy",
                              "Dummy",
                              LanguageNames.CSharp,
                              documents: new[] {
                                  DocumentInfo.Create(
                                      DocumentId.CreateNewId(projectId),
                                      "test.cs",
                                      loader: TextLoader.From(TextAndVersion.Create(SourceText.From("class A {}"), VersionStamp.Create(), filePath: "test.cs")),
                                      filePath: "test.cs")}));

            // create listener/service/analyzer
            var listener = new AsynchronousOperationListener();
            var service = new MyDiagnosticAnalyzerService(listener);

            var called = false;
            service.DiagnosticsUpdated += (s, e) =>
            {
                if (e.Diagnostics.Length == 0)
                {
                    return;
                }

                var liveId = (LiveDiagnosticUpdateArgsId)e.Id;
                Assert.False(liveId.Analyzer is ProjectDiagnosticAnalyzer);

                called = true;
            };

            var incrementalAnalyzer = (DiagnosticIncrementalAnalyzer)service.CreateIncrementalAnalyzer(workspace);
            await incrementalAnalyzer.AnalyzeProjectAsync(project, semanticsChanged: true, InvocationReasons.Reanalyze, CancellationToken.None);

            await listener.ExpeditedWaitAsync();

            Assert.True(called);
        }

        [Fact, WorkItem(42353, "https://github.com/dotnet/roslyn/issues/42353")]
        public async Task TestFullSolutionAnalysisForHiddenAnalyzers()
        {
            // By default, hidden analyzer does not execute in full solution analysis.
            using var workspace = CreateWorkspaceWithProjectAndAnalyzer(new NamedTypeAnalyzer(DiagnosticSeverity.Hidden));
            var project = workspace.CurrentSolution.Projects.Single();

            await TestFullSolutionAnalysisForProjectAsync(project, expectAnalyzerExecuted: false);
        }

        [Fact, WorkItem(42353, "https://github.com/dotnet/roslyn/issues/42353")]
        public async Task TestFullSolutionAnalysisForHiddenAnalyzers_SeverityInCompilationOptions()
        {
            // Escalating the analyzer to non-hidden effective severity through compilation options
            // ensures that analyzer executes in full solution analysis.
            using var workspace = CreateWorkspaceWithProjectAndAnalyzer(new NamedTypeAnalyzer(DiagnosticSeverity.Hidden));
            var project = workspace.CurrentSolution.Projects.Single();

            var newSpecificOptions = project.CompilationOptions.SpecificDiagnosticOptions.Add(NamedTypeAnalyzer.DiagnosticId, ReportDiagnostic.Warn);
            project = project.WithCompilationOptions(project.CompilationOptions.WithSpecificDiagnosticOptions(newSpecificOptions));
            await TestFullSolutionAnalysisForProjectAsync(project, expectAnalyzerExecuted: true);
        }

        [Fact, WorkItem(42353, "https://github.com/dotnet/roslyn/issues/42353")]
        public async Task TestFullSolutionAnalysisForHiddenAnalyzers_SeverityInAnalyzerConfigOptions()
        {
            using var workspace = CreateWorkspaceWithProjectAndAnalyzer(new NamedTypeAnalyzer(DiagnosticSeverity.Hidden));
            var project = workspace.CurrentSolution.Projects.Single();

            // Escalating the analyzer to non-hidden effective severity through analyzer config options
            // ensures that analyzer executes in full solution analysis.
            var analyzerConfigText = $@"
[*.cs]
dotnet_diagnostic.{NamedTypeAnalyzer.DiagnosticId}.severity = warning
";

            project = project.AddAnalyzerConfigDocument(
                ".editorconfig",
                text: SourceText.From(analyzerConfigText),
                filePath: "z:\\.editorconfig").Project;

            await TestFullSolutionAnalysisForProjectAsync(project, expectAnalyzerExecuted: true);
        }

        private static AdhocWorkspace CreateWorkspaceWithProjectAndAnalyzer(DiagnosticAnalyzer analyzer)
        {
            var workspace = new AdhocWorkspace();
            var projectId = ProjectId.CreateNewId();

            var solution = workspace.CurrentSolution;

            solution = solution
                .WithOptions(solution.Options.WithChangedOption(SolutionCrawlerOptions.BackgroundAnalysisScopeOption, LanguageNames.CSharp, BackgroundAnalysisScope.FullSolution))
                .AddAnalyzerReference(new AnalyzerImageReference(ImmutableArray.Create(analyzer)))
                .AddProject(
                    ProjectInfo.Create(
                        projectId,
                        VersionStamp.Create(),
                        "Dummy",
                        "Dummy",
                        LanguageNames.CSharp,
                        filePath: "z:\\Dummy.csproj",
                        documents: new[] {
                            DocumentInfo.Create(
                                DocumentId.CreateNewId(projectId),
                                "test.cs",
                                loader: TextLoader.From(TextAndVersion.Create(SourceText.From("class A {}"), VersionStamp.Create(), filePath: "test.cs")),
                                filePath: "z:\\test.cs")}));

            Assert.True(workspace.TryApplyChanges(solution));

            return workspace;
        }

        private static async Task TestFullSolutionAnalysisForProjectAsync(Project project, bool expectAnalyzerExecuted)
        {
            // create listener/service/analyzer
            var listener = new AsynchronousOperationListener();
            var service = new MyDiagnosticAnalyzerService(listener);

            var called = false;
            service.DiagnosticsUpdated += (s, e) =>
            {
                if (e.Diagnostics.Length == 0)
                {
                    return;
                }

                var liveId = (LiveDiagnosticUpdateArgsId)e.Id;
                Assert.True(liveId.Analyzer is NamedTypeAnalyzer);

                called = true;
            };

            var incrementalAnalyzer = (DiagnosticIncrementalAnalyzer)service.CreateIncrementalAnalyzer(project.Solution.Workspace);
            await incrementalAnalyzer.AnalyzeProjectAsync(project, semanticsChanged: true, InvocationReasons.Reanalyze, CancellationToken.None);

            await listener.ExpeditedWaitAsync();

            Assert.Equal(expectAnalyzerExecuted, called);
        }

        [Theory, CombinatorialData]
        internal async Task TestDiagnosticSuppressor(bool includeAnalyzer, bool includeSuppressor, BackgroundAnalysisScope analysisScope)
        {
            var analyzers = ArrayBuilder<DiagnosticAnalyzer>.GetInstance();
            if (includeAnalyzer)
            {
                analyzers.Add(new NamedTypeAnalyzer());
            }

            if (includeSuppressor)
            {
                analyzers.Add(new DiagnosticSuppressorForId(NamedTypeAnalyzer.DiagnosticId));
            }

            var analyzerReference = new AnalyzerImageReference(analyzers.ToImmutableArray());

            using var workspace = TestWorkspace.CreateCSharp("class A {}", exportProvider: EditorServicesUtil.ExportProvider);
            var options = workspace.Options.WithChangedOption(SolutionCrawlerOptions.BackgroundAnalysisScopeOption, LanguageNames.CSharp, analysisScope);
            workspace.SetOptions(options);

            workspace.TryApplyChanges(workspace.CurrentSolution.WithAnalyzerReferences(new[] { analyzerReference }));

            var project = workspace.CurrentSolution.Projects.Single();
            var document = project.Documents.Single();

            // create listener/service/analyzer
            var listener = new AsynchronousOperationListener();
            var service = new MyDiagnosticAnalyzerService(listener);

            DiagnosticData diagnostic = null;
            service.DiagnosticsUpdated += (s, e) =>
            {
                if (e.Diagnostics.Length == 0)
                {
                    return;
                }

                diagnostic = Assert.Single(e.Diagnostics);
            };

            var incrementalAnalyzer = (DiagnosticIncrementalAnalyzer)service.CreateIncrementalAnalyzer(workspace);

            switch (analysisScope)
            {
                case BackgroundAnalysisScope.ActiveFile:
                    workspace.OpenDocument(document.Id);
                    var documentTrackingService = (TestDocumentTrackingService)workspace.Services.GetService<IDocumentTrackingService>();
                    documentTrackingService.SetActiveDocument(document.Id);
                    await incrementalAnalyzer.AnalyzeDocumentAsync(document, bodyOpt: null, InvocationReasons.SemanticChanged, CancellationToken.None);
                    break;

                case BackgroundAnalysisScope.OpenFilesAndProjects:
                    workspace.OpenDocument(document.Id);
                    await incrementalAnalyzer.AnalyzeDocumentAsync(document, bodyOpt: null, InvocationReasons.SemanticChanged, CancellationToken.None);
                    break;

                case BackgroundAnalysisScope.FullSolution:
                    await incrementalAnalyzer.AnalyzeProjectAsync(project, semanticsChanged: true, InvocationReasons.Reanalyze, CancellationToken.None);
                    break;
            }

            await listener.ExpeditedWaitAsync();

            if (includeAnalyzer)
            {
                Assert.True(diagnostic != null);
                Assert.Equal(NamedTypeAnalyzer.DiagnosticId, diagnostic.Id);
                Assert.Equal(includeSuppressor, diagnostic.IsSuppressed);
            }
            else
            {
                Assert.True(diagnostic == null);
            }
        }

        [Theory, CombinatorialData]
        internal async Task TestPragmaSuppressionsAnalyzer(BackgroundAnalysisScope analysisScope)
        {
            var analyzers = ImmutableArray.Create<DiagnosticAnalyzer>(
                new CSharpCompilerDiagnosticAnalyzer(),
                new NamedTypeAnalyzer(),
                new CSharpRemoveUnnecessaryPragmaSuppressionsDiagnosticAnalyzer());

            var analyzerReference = new AnalyzerImageReference(analyzers);

            var code = $@"
#pragma warning disable {NamedTypeAnalyzer.DiagnosticId} // Unnecessary
#pragma warning disable CS0168 // Variable is declared but never used - Unnecessary

#pragma warning disable {NamedTypeAnalyzer.DiagnosticId} // Necessary
class A
{{
    void M()
    {{
#pragma warning disable CS0168 // Variable is declared but never used - Necessary
        int x;
    }}
}}
";

            using var workspace = TestWorkspace.CreateCSharp(code, exportProvider: EditorServicesUtil.ExportProvider);
            var options = workspace.Options.WithChangedOption(SolutionCrawlerOptions.BackgroundAnalysisScopeOption, LanguageNames.CSharp, analysisScope);
            workspace.SetOptions(options);

            workspace.TryApplyChanges(workspace.CurrentSolution.WithAnalyzerReferences(new[] { analyzerReference }));

            var project = workspace.CurrentSolution.Projects.Single();
            var document = project.Documents.Single();

            // create listener/service/analyzer
            var listener = new AsynchronousOperationListener();
            var service = new MyDiagnosticAnalyzerService(listener);

            var diagnostics = ArrayBuilder<DiagnosticData>.GetInstance();
            service.DiagnosticsUpdated += (s, e) =>
            {
                diagnostics.AddRange(e.Diagnostics.Where(d => d.Id == IDEDiagnosticIds.RemoveUnnecessarySuppressionDiagnosticId).OrderBy(d => d.GetTextSpan()));
            };

            var incrementalAnalyzer = (DiagnosticIncrementalAnalyzer)service.CreateIncrementalAnalyzer(workspace);

            switch (analysisScope)
            {
                case BackgroundAnalysisScope.ActiveFile:
                    workspace.OpenDocument(document.Id);
                    var documentTrackingService = (TestDocumentTrackingService)workspace.Services.GetService<IDocumentTrackingService>();
                    documentTrackingService.SetActiveDocument(document.Id);
                    await incrementalAnalyzer.AnalyzeDocumentAsync(document, bodyOpt: null, InvocationReasons.SemanticChanged, CancellationToken.None);
                    break;

                case BackgroundAnalysisScope.OpenFilesAndProjects:
                    workspace.OpenDocument(document.Id);
                    await incrementalAnalyzer.AnalyzeDocumentAsync(document, bodyOpt: null, InvocationReasons.SemanticChanged, CancellationToken.None);
                    break;

                case BackgroundAnalysisScope.FullSolution:
                    await incrementalAnalyzer.AnalyzeProjectAsync(project, semanticsChanged: true, InvocationReasons.Reanalyze, CancellationToken.None);
                    break;
            }

            await listener.ExpeditedWaitAsync();

            Assert.Equal(2, diagnostics.Count);
            var root = await document.GetSyntaxRootAsync();
            var pragma1 = root.FindTrivia(diagnostics[0].GetTextSpan().Start).ToString();
            Assert.Equal($"#pragma warning disable {NamedTypeAnalyzer.DiagnosticId} // Unnecessary", pragma1);
            var pragma2 = root.FindTrivia(diagnostics[1].GetTextSpan().Start).ToString();
            Assert.Equal($"#pragma warning disable CS0168 // Variable is declared but never used - Unnecessary", pragma2);
        }

        private static Document GetDocumentFromIncompleteProject(AdhocWorkspace workspace)
        {
            var project = workspace.AddProject(
                            ProjectInfo.Create(
                                ProjectId.CreateNewId(),
                                VersionStamp.Create(),
                                "CSharpProject",
                                "CSharpProject",
                                LanguageNames.CSharp).WithHasAllInformation(hasAllInformation: false));

            return workspace.AddDocument(project.Id, "Empty.cs", SourceText.From("class A { B B {get} }"));
        }

        private static (bool, bool) AnalyzerResultSetter(bool syntax, bool semantic, ImmutableArray<DiagnosticData> diagnostics)
        {
            switch (diagnostics.Length)
            {
                case 0:
                    break;
                case 1:
                    syntax |= diagnostics[0].Id == Analyzer.s_syntaxRule.Id;
                    semantic |= diagnostics[0].Id == Analyzer.s_semanticRule.Id;
                    break;
                default:
                    AssertEx.Fail("shouldn't reach here");
                    break;
            }

            return (syntax, semantic);
        }

        private static (bool, bool) CompilerAnalyzerResultSetter(bool syntax, bool semantic, ImmutableArray<DiagnosticData> diagnostics)
        {
            syntax |= diagnostics.Any(d => d.Properties["Origin"] == "Syntactic");
            semantic |= diagnostics.Any(d => d.Properties["Origin"] != "Syntactic");

            return (syntax, semantic);
        }

        private static async Task RunAllAnalysisAsync(IIncrementalAnalyzer analyzer, Document document)
        {
            await analyzer.AnalyzeSyntaxAsync(document, InvocationReasons.Empty, CancellationToken.None).ConfigureAwait(false);
            await analyzer.AnalyzeDocumentAsync(document, bodyOpt: null, reasons: InvocationReasons.Empty, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            await analyzer.AnalyzeProjectAsync(document.Project, semanticsChanged: true, reasons: InvocationReasons.Empty, cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }

        private class MyDiagnosticAnalyzerService : DiagnosticAnalyzerService
        {
            internal MyDiagnosticAnalyzerService(IAsynchronousOperationListener listener)
                : base(new MockDiagnosticUpdateSourceRegistrationService(), listener: listener)
            {
            }
        }

        private class Analyzer : DiagnosticAnalyzer
        {
            internal static readonly DiagnosticDescriptor s_syntaxRule = new DiagnosticDescriptor("syntax", "test", "test", "test", DiagnosticSeverity.Error, isEnabledByDefault: true);
            internal static readonly DiagnosticDescriptor s_semanticRule = new DiagnosticDescriptor("semantic", "test", "test", "test", DiagnosticSeverity.Error, isEnabledByDefault: true);
            internal static readonly DiagnosticDescriptor s_compilationRule = new DiagnosticDescriptor("compilation", "test", "test", "test", DiagnosticSeverity.Error, isEnabledByDefault: true);

            public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(s_syntaxRule, s_semanticRule, s_compilationRule);

            public override void Initialize(AnalysisContext context)
            {
                context.RegisterSyntaxTreeAction(c => c.ReportDiagnostic(Diagnostic.Create(s_syntaxRule, c.Tree.GetRoot().GetLocation())));
                context.RegisterSemanticModelAction(c => c.ReportDiagnostic(Diagnostic.Create(s_semanticRule, c.SemanticModel.SyntaxTree.GetRoot().GetLocation())));
                context.RegisterCompilationAction(c => c.ReportDiagnostic(Diagnostic.Create(s_compilationRule, c.Compilation.SyntaxTrees.First().GetRoot().GetLocation())));
            }
        }

        private class DisabledByDefaultAnalyzer : DiagnosticAnalyzer
        {
            internal static readonly DiagnosticDescriptor s_syntaxRule = new DiagnosticDescriptor("syntax", "test", "test", "test", DiagnosticSeverity.Error, isEnabledByDefault: false);
            internal static readonly DiagnosticDescriptor s_semanticRule = new DiagnosticDescriptor("semantic", "test", "test", "test", DiagnosticSeverity.Error, isEnabledByDefault: false);
            internal static readonly DiagnosticDescriptor s_compilationRule = new DiagnosticDescriptor("compilation", "test", "test", "test", DiagnosticSeverity.Error, isEnabledByDefault: false);

            public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(s_syntaxRule, s_semanticRule, s_compilationRule);

            public override void Initialize(AnalysisContext context)
            {
                context.RegisterSyntaxTreeAction(c => c.ReportDiagnostic(Diagnostic.Create(s_syntaxRule, c.Tree.GetRoot().GetLocation())));
                context.RegisterSemanticModelAction(c => c.ReportDiagnostic(Diagnostic.Create(s_semanticRule, c.SemanticModel.SyntaxTree.GetRoot().GetLocation())));
                context.RegisterCompilationAction(c => c.ReportDiagnostic(Diagnostic.Create(s_compilationRule, c.Compilation.SyntaxTrees.First().GetRoot().GetLocation())));
            }
        }

        private class OpenFileOnlyAnalyzer : DiagnosticAnalyzer, IBuiltInAnalyzer
        {
            internal static readonly DiagnosticDescriptor s_syntaxRule = new DiagnosticDescriptor("syntax", "test", "test", "test", DiagnosticSeverity.Error, isEnabledByDefault: true);

            public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(s_syntaxRule);

            public override void Initialize(AnalysisContext context)
                => context.RegisterSyntaxTreeAction(c => c.ReportDiagnostic(Diagnostic.Create(s_syntaxRule, c.Tree.GetRoot().GetLocation())));

            public DiagnosticAnalyzerCategory GetAnalyzerCategory()
                => DiagnosticAnalyzerCategory.SyntaxTreeWithoutSemanticsAnalysis;

            public bool OpenFileOnly(CodeAnalysis.Options.OptionSet options)
                => true;
        }

        private class NoNameAnalyzer : DocumentDiagnosticAnalyzer
        {
            internal static readonly DiagnosticDescriptor s_syntaxRule = new DiagnosticDescriptor("syntax", "test", "test", "test", DiagnosticSeverity.Error, isEnabledByDefault: true);

            public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(s_syntaxRule);

            public override Task<ImmutableArray<Diagnostic>> AnalyzeSyntaxAsync(Document document, CancellationToken cancellationToken)
                => Task.FromResult(ImmutableArray.Create(Diagnostic.Create(s_syntaxRule, Location.Create(document.FilePath, TextSpan.FromBounds(0, 0), new LinePositionSpan(new LinePosition(0, 0), new LinePosition(0, 0))))));

            public override Task<ImmutableArray<Diagnostic>> AnalyzeSemanticsAsync(Document document, CancellationToken cancellationToken)
                => SpecializedTasks.Default<ImmutableArray<Diagnostic>>();
        }

        private class Priority20Analyzer : PriorityTestDocumentDiagnosticAnalyzer
        {
            public Priority20Analyzer() : base(priority: 20) { }
        }

        private class Priority15Analyzer : PriorityTestProjectDiagnosticAnalyzer
        {
            public Priority15Analyzer() : base(priority: 15) { }
        }

        private class Priority10Analyzer : PriorityTestDocumentDiagnosticAnalyzer
        {
            public Priority10Analyzer() : base(priority: 10) { }
        }

        private class Priority1Analyzer : PriorityTestProjectDiagnosticAnalyzer
        {
            public Priority1Analyzer() : base(priority: 1) { }
        }

        private class Priority0Analyzer : PriorityTestDocumentDiagnosticAnalyzer
        {
            public Priority0Analyzer() : base(priority: -1) { }
        }

        private class PriorityTestDocumentDiagnosticAnalyzer : DocumentDiagnosticAnalyzer
        {
            protected PriorityTestDocumentDiagnosticAnalyzer(int priority)
                => Priority = priority;

            public override int Priority { get; }
            public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray<DiagnosticDescriptor>.Empty;
            public override Task<ImmutableArray<Diagnostic>> AnalyzeSemanticsAsync(Document document, CancellationToken cancellationToken)
                => Task.FromResult(ImmutableArray<Diagnostic>.Empty);
            public override Task<ImmutableArray<Diagnostic>> AnalyzeSyntaxAsync(Document document, CancellationToken cancellationToken)
                => Task.FromResult(ImmutableArray<Diagnostic>.Empty);
        }

        private class PriorityTestProjectDiagnosticAnalyzer : ProjectDiagnosticAnalyzer
        {
            protected PriorityTestProjectDiagnosticAnalyzer(int priority)
                => Priority = priority;

            public override int Priority { get; }
            public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray<DiagnosticDescriptor>.Empty;
            public override Task<ImmutableArray<Diagnostic>> AnalyzeProjectAsync(Project project, CancellationToken cancellationToken)
                => Task.FromResult(ImmutableArray<Diagnostic>.Empty);
        }

        private class LeakDocumentAnalyzer : DocumentDiagnosticAnalyzer
        {
            internal static readonly DiagnosticDescriptor s_syntaxRule = new DiagnosticDescriptor("leak", "test", "test", "test", DiagnosticSeverity.Error, isEnabledByDefault: true);

            public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(s_syntaxRule);

            public override async Task<ImmutableArray<Diagnostic>> AnalyzeSyntaxAsync(Document document, CancellationToken cancellationToken)
            {
                var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                return ImmutableArray.Create(Diagnostic.Create(s_syntaxRule, root.GetLocation()));
            }

            public override Task<ImmutableArray<Diagnostic>> AnalyzeSemanticsAsync(Document document, CancellationToken cancellationToken)
                => SpecializedTasks.Default<ImmutableArray<Diagnostic>>();
        }

        private class LeakProjectAnalyzer : ProjectDiagnosticAnalyzer
        {
            private static readonly DiagnosticDescriptor s_rule = new DiagnosticDescriptor("project", "test", "test", "test", DiagnosticSeverity.Error, isEnabledByDefault: true);
            public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(s_rule);
            public override Task<ImmutableArray<Diagnostic>> AnalyzeProjectAsync(Project project, CancellationToken cancellationToken) => SpecializedTasks.Default<ImmutableArray<Diagnostic>>();
        }

        [DiagnosticAnalyzer(LanguageNames.CSharp)]
        private class NamedTypeAnalyzer : DiagnosticAnalyzer
        {
            public const string DiagnosticId = "test";
            private readonly ImmutableArray<DiagnosticDescriptor> _supportedDiagnostics;

            public NamedTypeAnalyzer(DiagnosticSeverity defaultSeverity = DiagnosticSeverity.Warning)
                => _supportedDiagnostics = ImmutableArray.Create(new DiagnosticDescriptor(DiagnosticId, "test", "test", "test", defaultSeverity, isEnabledByDefault: true));

            public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => _supportedDiagnostics;

            public override void Initialize(AnalysisContext context)
            {
                context.RegisterSymbolAction(c =>
                {
                    c.ReportDiagnostic(Diagnostic.Create(_supportedDiagnostics[0], c.Symbol.Locations[0]));
                }, SymbolKind.NamedType);
            }
        }
    }
}
