using DynamicData;
using Synthesis.Bethesda.Execution.Settings;
using Noggog;
using Noggog.WPF;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Windows.Input;
using System.Threading;
using Serilog;
using Synthesis.Bethesda.Execution.Patchers.Git;

namespace Synthesis.Bethesda.GUI
{
    public abstract class PatcherVM : ViewModel
    {
        public ProfileVM Profile { get; }

        private readonly ObservableAsPropertyHelper<bool> _IsSelected;
        public bool IsSelected => _IsSelected.Value;

        public int InternalID { get; }

        [Reactive]
        public bool IsOn { get; set; } = true;

        [Reactive]
        public string Name { get; set; } = string.Empty;

        public ICommand DeleteCommand { get; }

        protected abstract ConfigurationState InternalState { get; }

        private static int NextID;

        private readonly ObservableAsPropertyHelper<ConfigurationState> _State;
        public ConfigurationState State => _State.Value;

        public PatcherVM(ProfileVM parent, PatcherSettings? settings)
        {
            InternalID = Interlocked.Increment(ref NextID);

            Profile = parent;
            _IsSelected = this.WhenAnyValue(x => x.Profile.Config.SelectedPatcher)
                .Select(x => x == this)
                .ToGuiProperty(this, nameof(IsSelected));

            // Set to settings
            IsOn = settings?.On ?? false;
            Name = settings?.Nickname ?? string.Empty;

            DeleteCommand = ReactiveCommand.Create(() =>
            {
                parent.Config.MainVM.ActiveConfirmation = new ConfirmationActionVM(
                    "Confirm",
                    $"Are you sure you want to delete {Name}?",
                    Delete);
            });

            _State = Observable.CombineLatest(
                    this.WhenAnyValue(x => x.InternalState),
                    parent.Patchers.Connect()
                        .QueryWhenChanged(q =>
                        {
                            foreach (var patcher in q)
                            {
                                if (!ReferenceEquals(this, patcher) && patcher.Name.Equals(this.Name))
                                {
                                    return ErrorResponse.Fail("Duplicate name");
                                }
                            }
                            return ErrorResponse.Success;
                        })
                        .Select(e => new ConfigurationState(e)),
                    (subState, dup) => ConfigurationState.Combine(subState, dup))
                .ToGuiProperty(this, nameof(State), ConfigurationState.Evaluating);
        }

        public abstract PatcherSettings Save();

        protected void CopyOverSave(PatcherSettings settings)
        {
            settings.On = IsOn;
            settings.Nickname = Name;
        }

        public abstract PatcherRunVM ToRunner(PatchersRunVM parent);

        public virtual void Delete()
        {
            Profile.Patchers.Remove(this);
        }

        protected ILogger Logger => Log.Logger.ForContext(nameof(Name), Name);

        public abstract string GetDefaultName();
    }
}
