using System;
using Microsoft.UI.Xaml;

namespace RimePPT
{
    /// <summary>
    /// 隐藏的应用锚点窗口：托盘与设置窗承载全部交互，此窗口永不显示，
    /// 仅用于维持应用生命周期。
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
            {
                presenter.IsMinimizable = false;
                presenter.IsMaximizable = false;
            }

            AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico"));

            // 隐藏：本窗口不参与交互
            AppWindow.Hide();
        }
    }
}
