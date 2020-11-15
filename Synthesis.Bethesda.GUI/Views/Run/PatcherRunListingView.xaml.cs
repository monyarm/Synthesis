using Noggog.WPF;
using ReactiveUI;
using System.Reactive.Disposables;
using System.Windows;
using System.Reactive.Linq;

namespace Synthesis.Bethesda.GUI.Views
{
    public class PatcherRunListingViewBase : NoggogUserControl<PatcherRunVM> { }

    /// <summary>
    /// Interaction logic for PatcherRunListingView.xaml
    /// </summary>
    public partial class PatcherRunListingView : PatcherRunListingViewBase
    {
        public PatcherRunListingView()
        {
            InitializeComponent();
            this.WhenActivated(disposable =>
            {
                this.WhenAnyValue(x => x.ViewModel!.Config.Name)
                    .BindToStrict(this, x => x.NameBlock.Text)
                    .DisposeWith(disposable);
                this.WhenAnyValue(x => x.ViewModel!.IsSelected)
                    .Select(x => x ? Visibility.Visible : Visibility.Collapsed)
                    .BindToStrict(this, x => x.SelectedGlow.Visibility)
                    .DisposeWith(disposable);
                this.WhenAnyValue(x => x.ViewModel!.RunTimeString)
                    .BindToStrict(this, x => x.RunningTimeBlock.Text)
                    .DisposeWith(disposable);
            });
        }
    }
}
