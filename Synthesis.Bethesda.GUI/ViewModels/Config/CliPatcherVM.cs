using Noggog;
using Noggog.WPF;
using ReactiveUI;
using Synthesis.Bethesda.Execution.Patchers;
using Synthesis.Bethesda.Execution.Patchers.Git;
using Synthesis.Bethesda.Execution.Settings;
using System;
using System.IO;
using System.Reactive.Linq;

namespace Synthesis.Bethesda.GUI
{
    public class CliPatcherVM : PatcherVM
    {
        public readonly PathPickerVM PathToExecutable = new PathPickerVM()
        {
             PathType = PathPickerVM.PathTypeOptions.File,
             ExistCheckOption = PathPickerVM.CheckOptions.On,
        };

        private readonly ObservableAsPropertyHelper<ConfigurationState> _InternalState;
        protected override ConfigurationState InternalState => _InternalState?.Value ?? ConfigurationState.Evaluating;

        public CliPatcherVM(ProfileVM parent, CliPatcherSettings? settings = null)
            : base(parent, settings)
        {
            CopyInSettings(settings);

            _InternalState = this.WhenAnyValue(x => x.PathToExecutable.ErrorState)
                .Select(e =>
                {
                    return new ConfigurationState()
                    {
                        IsHaltingError = !e.Succeeded,
                        RunnableState = e
                    };
                })
                .ToGuiProperty<ConfigurationState>(this, nameof(InternalState), new ConfigurationState(ErrorResponse.Fail("Evaluating"))
                {
                    IsHaltingError = false
                });
        }
        private void CopyInSettings(CliPatcherSettings? settings)
        {
            if (settings == null) return;
            PathToExecutable.TargetPath = settings.PathToExecutable;
        }

        public override PatcherSettings Save()
        {
            var ret = new CliPatcherSettings();
            CopyOverSave(ret);
            ret.PathToExecutable = PathToExecutable.TargetPath;
            return ret;
        }

        public override PatcherRunVM ToRunner(PatchersRunVM parent)
        {
            return new PatcherRunVM(
                parent, 
                this, 
                new CliPatcherRun(
                    nickname: Name, 
                    pathToExecutable: PathToExecutable.TargetPath, 
                    pathToExtra: null));
        }

        public override string GetDefaultName()
        {
            try
            {
                return Path.GetFileNameWithoutExtension(PathToExecutable.TargetPath);
            }
            catch (Exception)
            {
                return "<Naming Error>";
            }
        }
    }
}
