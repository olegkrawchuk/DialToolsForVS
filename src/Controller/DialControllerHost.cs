using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using EnvDTE80;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI.Input;
using DialControllerTools.Helpers;

using Task = System.Threading.Tasks.Task;

namespace DialControllerTools
{
    internal class DialControllerHost : IDialControllerHost
    {
        private static readonly Dictionary<EnvDTE.Window, RadialController> controllersMapping = new Dictionary<EnvDTE.Window, RadialController>();
        private static readonly Dictionary<Window, RadialController> childrenControllersMapping = new Dictionary<Window, RadialController>();
        private static readonly ShellHook _hooks = new ShellHook();

        private readonly DTE2 dte;
        public DTE2 DTE => dte;

        private readonly IComponentModel _compositionService;
        public IComponentModel CompositionService => _compositionService;

        private Logger Logger => Logger.Instance;


        private readonly IAsyncServiceProvider serviceProvider;

        private RadialControllerStatusBarHost _status;
        private ImmutableArray<IDialController> _controllers;
        private bool _firstActivation = true;

        [ImportMany(typeof(IDialControllerProvider))]
        private IEnumerable<Lazy<IDialControllerProvider, IDialMetadata>> _providers { get; set; }

        public static DialControllerHost Instance
        {
            get;
            private set;
        }

        private DialControllerHost(IAsyncServiceProvider provider, DTE2 dte, IComponentModel compositionService)
        {
            this.dte = dte;
            this._compositionService = compositionService;
            this.serviceProvider = provider;
            compositionService.DefaultCompositionService.SatisfyImportsOnce(this);
        }

        public static async Task InitializeAsync(IAsyncServiceProvider provider, JoinableTask optionsLoadTask, CancellationToken cancellationToken)
        {
            var dte = await provider.GetDteAsync(cancellationToken);
            var compositionService = await provider.GetServiceAsync<SComponentModel, IComponentModel>(cancellationToken);

            Instance = new DialControllerHost(provider, dte, compositionService);
            var mainWindow = dte.DTE.MainWindow;
            await Task.WhenAll(
                ThreadHelper.JoinableTaskFactory.StartOnIdle(Instance.CreateStatusBarItem).JoinAsync(),
                ThreadHelper.JoinableTaskFactory.StartOnIdle(new Action(() => controllersMapping.Add(mainWindow, Instance.CreateController(new IntPtr(mainWindow.HWnd))))).JoinAsync());
            await optionsLoadTask;
            await Instance.ImportProvidersAsync(cancellationToken);


            var messageCallbacks = new Dictionary<WH_SHELL_MESSAGES, Action<IntPtr>>();
            messageCallbacks[WH_SHELL_MESSAGES.HSHELL_WINDOWCREATED] = CreateCallback;

            var success = _hooks.Set(messageCallbacks);
            //if (!success)
            //{
            //    Logger.Instance.Log("ERROR SET HOOK");
            //}
        }

        private static void CreateCallback(IntPtr hwnd)
        {
            var hwndSource = HwndSource.FromHwnd(hwnd);

            var window = hwndSource.RootVisual as Window;
            window.Closed += Window_Closed;

            childrenControllersMapping[window] = Instance.CreateController(hwnd);

            Logger.Instance.Log($"CREATED: title - {window.Title}");
        }

        private static void Window_Closed(object sender, EventArgs e)
        {
            var window = sender as Window;
            if (childrenControllersMapping.TryGetValue(window, out var controller))
            {
                Instance.UnsubscribeFromController(controller);
                childrenControllersMapping.Remove(window);
                Logger.Instance.Log($"CLOSED: title - {window.Title}");
            }
        }

        private void CreateStatusBarItem()
        {
            _status = new RadialControllerStatusBarHost { Name = "PART_DialControllerHost" };
            var injector = new StatusBarInjector(Application.Current.MainWindow);
            injector.InjectControl(_status);
        }

        internal RadialController CreateController(IntPtr hWnd)
        {
            var interop = (IRadialControllerInterop)WindowsRuntimeMarshal.GetActivationFactory(typeof(RadialController));
            Guid guid = typeof(RadialController).GetInterface("IRadialController").GUID;

            var radialController = interop.CreateForWindow(hWnd, ref guid);
            radialController.RotationChanged += OnRotationChanged;
            radialController.ButtonClicked += OnButtonClicked;
            radialController.ControlAcquired += OnControlAcquired;
            radialController.ControlLost += OnControlLost;

            if (radialController == null)
                Logger.Log("Couldn't create RadialController");

            return radialController;
        }

        internal void UnsubscribeFromController(RadialController radialController)
        {
            radialController.RotationChanged -= OnRotationChanged;
            radialController.ButtonClicked -= OnButtonClicked;
            radialController.ControlAcquired -= OnControlAcquired;
            radialController.ControlLost -= OnControlLost;
        }

        private static JoinableTask SetDefaultItemsAsync() => ThreadHelper.JoinableTaskFactory.StartOnIdle(() =>
        {
            RadialControllerConfiguration config;
            var radialControllerConfigInterop = (IRadialControllerConfigurationInterop)WindowsRuntimeMarshal.GetActivationFactory(typeof(RadialControllerConfiguration));
            Guid guid = typeof(RadialControllerConfiguration).GetInterface("IRadialControllerConfiguration").GUID;

            foreach (var window in controllersMapping.Keys)
            {
                config = radialControllerConfigInterop.GetForWindow(new IntPtr(window.HWnd), ref guid);
                config.SetDefaultMenuItems(new RadialControllerSystemMenuItemKind[0]);
            }
        });

        internal void OptionsApplied(object sender, EventArgs e)
        {
            foreach (var controller in controllersMapping.Values)
            {
                controller.Menu.Items.Clear();
            }
            ThreadHelper.JoinableTaskFactory.Run(() => ImportProvidersAsync());
        }

        internal async Task ImportProvidersAsync(CancellationToken cancellationToken = default)
        {
            var tasks = _providers
                .Select(async provider =>
                {
                    var controller = await provider.Value.TryCreateControllerAsync(this, serviceProvider, cancellationToken);
                    return (Controller: controller, provider.Metadata.Order);
                });
            _controllers = (await Task.WhenAll(tasks))
                           .Where(result => result.Controller != null)
                           .OrderBy(result => result.Order)
                           .Select(result => result.Controller)
                           .ToImmutableArray();

            await SetDefaultItemsAsync();
        }

        public Task AddMenuItemAsync(string moniker, string iconFilePath)
        {
            if (!DialPackage.Options.MenuVisibility[moniker])
                return Task.CompletedTask;

            return AddMenuItemInternalAsync(moniker, iconFilePath);
        }

        private async Task AddMenuItemInternalAsync(string moniker, string iconFilePath)
        {
            await Task.Yield();
            await TaskScheduler.Default;

            StorageFile file = await StorageFile.GetFileFromPathAsync(iconFilePath);

            var stream = RandomAccessStreamReference.CreateFromFile(file);
            var menuItem = RadialControllerMenuItem.CreateFromIcon(moniker, stream);

            menuItem.Invoked += (sender, args) =>
            {
                _status.Text = sender.DisplayText;
                _controllers.FirstOrDefault(c => c.Moniker == moniker)?.OnActivate();
            };

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            foreach (var controller in controllersMapping.Values)
                if (!controller.Menu.Items.Any(i => i.DisplayText == moniker))
                {
                    controller.Menu.Items.Add(menuItem);
                }
        }

        public void RemoveMenuItem(string moniker)
        {
            foreach (var controller in controllersMapping.Values)
            {
                RadialControllerMenuItem item = controller.Menu.Items.FirstOrDefault(i => i.DisplayText == moniker);

                if (item != null) controller.Menu.Items.Remove(item);

            }
        }

        public void RequestActivation(RadialController controller, string moniker)
        {
            RadialControllerMenuItem item = controller.Menu.Items.FirstOrDefault(i => i.DisplayText == moniker);

            if (item != null)
            {
                controller.Menu.SelectMenuItem(item);
                _status.Text = item.DisplayText;
            }
        }

        public void RequestActivation(EnvDTE.Window window, string moniker)
         => RequestActivation(controllersMapping[window], moniker);

        public void ReleaseActivation(RadialController controller)
        {
            controller.Menu.TrySelectPreviouslySelectedMenuItem();
        }

        public void ReleaseActivation(EnvDTE.Window window)
         => ReleaseActivation(controllersMapping[window]);

        private void OnControlAcquired(RadialController sender, RadialControllerControlAcquiredEventArgs args)
        {
            if (_firstActivation)
            {
                _firstActivation = false;
                var defaultMenu = DialPackage.Options.DefaultProvider.ToString();
                if (!DialPackage.Options.MenuVisibility[defaultMenu])
                {
                    defaultMenu = DialPackage.Options.MenuVisibility.FirstOrDefault(_ => _.Value).Key ?? nameof(KnownProviders.Scroll);
                }
                RequestActivation(sender, defaultMenu);
            }

            Debug.Assert(_status != null);
            _status.Text = sender.Menu.GetSelectedMenuItem()?.DisplayText;
        }

        private void OnControlLost(RadialController sender, object args)
        {
            Debug.Assert(_status != null);
            _status.IsActive = false;
        }

        private void OnButtonClicked(RadialController sender, RadialControllerButtonClickedEventArgs args)
        {
            IEnumerable<IDialController> controllers = GetApplicableControllers(sender).Where(c => c.CanHandleClick);
            Logger.Log("Click: " + string.Join(", ", controllers.Select(c => c.Moniker)));

            foreach (IDialController controller in controllers)
            {
                try
                {
                    bool handled = controller.OnClick();

                    if (handled)
                        break;
                }
                catch (Exception ex)
                {
                    Logger.Log(ex);
                }
            }
        }

        private void OnRotationChanged(RadialController sender, RadialControllerRotationChangedEventArgs args)
        {
            IEnumerable<IDialController> controllers = GetApplicableControllers(sender).Where(c => c.CanHandleRotate);
            RotationDirection direction = args.RotationDeltaInDegrees > 0 ? RotationDirection.Right : RotationDirection.Left;

            Logger.Log("Rotate: " + string.Join(", ", controllers.Select(c => c.Moniker)));

            foreach (IDialController controller in controllers)
            {
                try
                {
                    bool handled = controller.OnRotate(direction);

                    if (handled)
                        break;
                }
                catch (Exception ex)
                {
                    Logger.Log(ex);
                }
            }
        }

        private IEnumerable<IDialController> GetApplicableControllers(RadialController controller)
        {
            string moniker = controller.Menu.GetSelectedMenuItem()?.DisplayText;

            if (string.IsNullOrEmpty(moniker))
                Enumerable.Empty<IDialController>();

            try
            {
                return _controllers.Where(c => c.Moniker == moniker);
            }
            catch (Exception ex)
            {
                Logger.Log(ex);
                return Enumerable.Empty<IDialController>();
            }
        }
    }
}
