using System;
using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DialToolsForVS
{
    internal class StatusBarInjector
    {
        private Window _window;
        private FrameworkElement _statusBar;
        private Panel _panel;

        public StatusBarInjector(Window pWindow)
        {
            _window = pWindow;
            _window.Initialized += new EventHandler(Window_Initialized);

            FindStatusBar();
        }

        private static DependencyObject FindChild(DependencyObject parent, string childName)
        {
            if (parent == null)
            {
                return null;
            }

            int childrenCount = VisualTreeHelper.GetChildrenCount(parent);

            for (int i = 0; i < childrenCount; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);

                if (child is FrameworkElement frameworkElement && frameworkElement.Name == childName)
                {
                    return frameworkElement;
                }

                child = StatusBarInjector.FindChild(child, childName);

                if (child != null)
                {
                    return child;
                }
            }

            return null;
        }

        private void FindStatusBar()
        {
            _statusBar = FindChild(_window, "StatusBarContainer") as FrameworkElement;
            _panel = _statusBar.Parent as DockPanel;
        }

        private static FrameworkElement FindStatusBarContainer(Panel panel)
        {
            FrameworkElement frameworkElement;
            IEnumerator enumerator = panel.Children.GetEnumerator();

            try
            {
                while (enumerator.MoveNext())
                {
                    var current = enumerator.Current as FrameworkElement;

                    if (current == null || !(current.Name == "StatusBarContainer"))
                    {
                        continue;
                    }

                    frameworkElement = current;
                    return frameworkElement;
                }

                return null;
            }
            finally
            {
                if (enumerator is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
        }

        public void InjectControl(FrameworkElement pControl)
        {
            _panel.Dispatcher.Invoke(() =>
            {
                pControl.SetValue(DockPanel.DockProperty, Dock.Left);
                _panel.Children.Insert(1, pControl);
            });
        }

        public bool IsInjected(FrameworkElement pControl)
        {
            bool flag2 = false;

            _panel.Dispatcher.Invoke(() =>
            {
                bool flag = _panel.Children.Contains(pControl);
                bool flag1 = flag;
                flag2 = flag;
                return flag1;
            });

            return flag2;
        }

        public void UninjectControl(FrameworkElement pControl)
        {
            _panel.Dispatcher.Invoke(() => _panel.Children.Remove(pControl));
        }

        private void Window_Initialized(object sender, EventArgs e)
        {
        }
    }
}