using DynamicData;
using Microsoft.WindowsAPICodePack.Dialogs;
using Synthesis.Bethesda.Execution.Settings;
using Noggog;
using Noggog.WPF;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reactive.Linq;
using System.Text;
using System.Linq;
using DynamicData.Binding;
using System.Windows.Input;
using System.Diagnostics;
using System.Reactive;
using Newtonsoft.Json;
using Synthesis.Bethesda.DTO;
using Synthesis.Bethesda.Execution.Patchers.Git;
using Synthesis.Bethesda.Execution.Patchers;

namespace Synthesis.Bethesda.GUI
{
    public class SolutionPatcherVM : PatcherVM
    {
        public PathPickerVM SolutionPath { get; } = new PathPickerVM()
        {
            ExistCheckOption = PathPickerVM.CheckOptions.On,
            PathType = PathPickerVM.PathTypeOptions.File,
        };

        public IObservableCollection<string> AvailableProjects { get; }

        [Reactive]
        public string ProjectSubpath { get; set; } = string.Empty;

        public PathPickerVM SelectedProjectPath { get; } = new PathPickerVM()
        {
            ExistCheckOption = PathPickerVM.CheckOptions.On,
            PathType = PathPickerVM.PathTypeOptions.File,
        };

        private readonly ObservableAsPropertyHelper<ConfigurationState> _InternalState;
        protected override ConfigurationState InternalState => _InternalState?.Value ?? ConfigurationState.Evaluating;

        public ICommand OpenSolutionCommand { get; }

        [Reactive]
        public string ShortDescription { get; set; } = string.Empty;

        [Reactive]
        public string LongDescription { get; set; } = string.Empty;

        [Reactive]
        public VisibilityOptions Visibility { get; set; }

        [Reactive]
        public PreferredAutoVersioning Versioning { get; set; }

        public ObservableCollectionExtended<PreferredAutoVersioning> VersioningOptions { get; } = new ObservableCollectionExtended<PreferredAutoVersioning>(EnumExt.GetValues<PreferredAutoVersioning>());

        public ObservableCollectionExtended<VisibilityOptions> VisibilityOptions { get; } = new ObservableCollectionExtended<VisibilityOptions>(EnumExt.GetValues<VisibilityOptions>());

        public SolutionPatcherVM(ProfileVM parent, SolutionPatcherSettings? settings = null)
            : base(parent, settings)
        {
            CopyInSettings(settings);
            SolutionPath.Filters.Add(new CommonFileDialogFilter("Solution", ".sln"));
            SelectedProjectPath.Filters.Add(new CommonFileDialogFilter("Project", ".csproj"));

            AvailableProjects = SolutionPatcherConfigLogic.AvailableProject(
                this.WhenAnyValue(x => x.SolutionPath.TargetPath))
                .ObserveOnGui()
                .ToObservableCollection(this);

            var projPath = SolutionPatcherConfigLogic.ProjectPath(
                solutionPath: this.WhenAnyValue(x => x.SolutionPath.TargetPath),
                projectSubpath: this.WhenAnyValue(x => x.ProjectSubpath));
            projPath
                .Subscribe(p => SelectedProjectPath.TargetPath = p)
                .DisposeWith(this);

            _InternalState = Observable.CombineLatest(
                    this.WhenAnyValue(x => x.SolutionPath.ErrorState),
                    this.WhenAnyValue(x => x.SelectedProjectPath.ErrorState),
                    this.WhenAnyValue(x => x.Profile.Config.MainVM)
                        .Select(x => x.DotNetSdkInstalled)
                        .Switch(),
                    (sln, proj, dotnet) =>
                    {
                        if (sln.Failed) return new ConfigurationState(sln);
                        if (dotnet == null) return new ConfigurationState(ErrorResponse.Fail("No dotnet SDK installed"));
                        return new ConfigurationState(proj);
                    })
                .ToGuiProperty<ConfigurationState>(this, nameof(InternalState), new ConfigurationState(ErrorResponse.Fail("Evaluating"))
                {
                    IsHaltingError = false
                });

            OpenSolutionCommand = ReactiveCommand.Create(
                canExecute: this.WhenAnyValue(x => x.SolutionPath.InError)
                    .Select(x => !x),
                execute: () =>
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo(SolutionPath.TargetPath)
                        {
                            UseShellExecute = true,
                        });
                    }
                    catch (Exception ex)
                    {
                        Log.Logger.Error(ex, $"Error opening solution: {SolutionPath.TargetPath}");
                    }
                });

            var metaPath = this.WhenAnyValue(x => x.SelectedProjectPath.TargetPath)
                .Select(projPath =>
                {
                    try
                    {
                        return Path.Combine(Path.GetDirectoryName(projPath)!, Constants.MetaFileName);
                    }
                    catch (Exception)
                    {
                        return string.Empty;
                    }
                })
                .Replay(1)
                .RefCount();

            // Set up meta file sync
            metaPath
                .Select(path =>
                {
                    return Noggog.ObservableExt.WatchFile(path)
                        .StartWith(Unit.Default)
                        .Select(_ =>
                        {
                            try
                            {
                                return JsonConvert.DeserializeObject<PatcherCustomization>(
                                    File.ReadAllText(path),
                                    Execution.Constants.JsonSettings);
                            }
                            catch (Exception ex)
                            {
                                Logger.Error(ex, "Error reading in meta");
                            }
                            return default(PatcherCustomization?);
                        });
                })
                .Switch()
                .DistinctUntilChanged()
                .ObserveOnGui()
                .Subscribe(info =>
                {
                    if (info == null) return;
                    if (info.Nickname != null)
                    {
                        this.Name = info.Nickname;
                    }
                    this.LongDescription = info.LongDescription ?? string.Empty;
                    this.ShortDescription = info.OneLineDescription ?? string.Empty;
                    this.Visibility = info.Visibility;
                    this.Versioning = info.PreferredAutoVersioning;
                })
                .DisposeWith(this);

            Observable.CombineLatest(
                    this.WhenAnyValue(x => x.Name),
                    this.WhenAnyValue(x => x.ShortDescription),
                    this.WhenAnyValue(x => x.LongDescription),
                    this.WhenAnyValue(x => x.Visibility),
                    this.WhenAnyValue(x => x.Versioning),
                    metaPath,
                    (nickname, shortDesc, desc, visibility, versioning, meta) => (nickname, shortDesc, desc, visibility, versioning, meta))
                .DistinctUntilChanged()
                .Throttle(TimeSpan.FromMilliseconds(200), RxApp.MainThreadScheduler)
                .Skip(1)
                .Subscribe(x =>
                {
                    try
                    {
                        if (string.IsNullOrWhiteSpace(x.meta)) return;
                        File.WriteAllText(x.meta,
                            JsonConvert.SerializeObject(
                                new PatcherCustomization()
                                {
                                    OneLineDescription = x.shortDesc,
                                    LongDescription = x.desc,
                                    Visibility = x.visibility,
                                    Nickname = x.nickname,
                                    PreferredAutoVersioning = x.versioning
                                },
                                Formatting.Indented,
                                Execution.Constants.JsonSettings));
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(ex, "Error writing out meta");
                    }
                })
                .DisposeWith(this);
        }

        public override PatcherSettings Save()
        {
            var ret = new SolutionPatcherSettings();
            CopyOverSave(ret);
            ret.SolutionPath = this.SolutionPath.TargetPath;
            ret.ProjectSubpath = this.ProjectSubpath;
            return ret;
        }

        private void CopyInSettings(SolutionPatcherSettings? settings)
        {
            if (settings == null) return;
            this.SolutionPath.TargetPath = settings.SolutionPath;
            this.ProjectSubpath = settings.ProjectSubpath;
        }

        public override PatcherRunVM ToRunner(PatchersRunVM parent)
        {
            return new PatcherRunVM(
                parent,
                this,
                new SolutionPatcherRun(
                    name: Name,
                    pathToSln: SolutionPath.TargetPath,
                    pathToExtraDataBaseFolder: Execution.Constants.TypicalExtraData,
                    pathToProj: SelectedProjectPath.TargetPath));
        }

        public override string GetDefaultName()
        {
            try
            {
                var name = Path.GetFileName(Path.GetDirectoryName(SelectedProjectPath.TargetPath));
                if (string.IsNullOrWhiteSpace(name)) return string.Empty;
                return name;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        public class SolutionPatcherConfigLogic
        {
            public static IObservable<IChangeSet<string>> AvailableProject(IObservable<string> solutionPath)
            {
                return solutionPath
                    .ObserveOn(RxApp.TaskpoolScheduler)
                    .Select(SolutionPatcherRun.AvailableProjects)
                    .Select(x => x.AsObservableChangeSet())
                    .Switch()
                    .RefCount();
            }

            public static IObservable<string> ProjectPath(IObservable<string> solutionPath, IObservable<string> projectSubpath)
            {
                return projectSubpath
                    // Need to throttle, as bindings flip to null quickly, which we want to skip
                    .Throttle(TimeSpan.FromMilliseconds(150), RxApp.MainThreadScheduler)
                    .DistinctUntilChanged()
                    .CombineLatest(solutionPath.DistinctUntilChanged(),
                        (subPath, slnPath) =>
                        {
                            if (subPath == null || slnPath == null) return string.Empty;
                            try
                            {
                                return Path.Combine(Path.GetDirectoryName(slnPath)!, subPath);
                            }
                            catch (Exception)
                            {
                                return string.Empty;
                            }
                        })
                    .Replay(1)
                    .RefCount();
            }
        }
    }
}
