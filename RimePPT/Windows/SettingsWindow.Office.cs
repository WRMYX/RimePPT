using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RimePPT.Core;
using RimePPT.Services;

namespace RimePPT.Windows;

public sealed partial class SettingsWindow
{
    private bool _officeBusy, _officeRefreshing;

    private async Task RefreshOfficeAddinAsync()
    {
        if (_closed || _officeRefreshing) return;
        _officeRefreshing = true;
        try
        {
            var status = await Task.Run(OfficeAddinService.ReadStatus);
            if (_closed) return;
            OfficeRegistrationText.Text = status.Registration;
            OfficeInstalledPath.Text = status.InstalledPath;
            if (OfficeScopeCombo.SelectedIndex < 0) OfficeScopeCombo.SelectedIndex = status.RecommendAllUsers ? 1 : 0;
            OfficeRegisterButton.IsEnabled = !_officeBusy && status.PayloadAvailable && !status.PowerPointRunning;
            OfficeUnregisterButton.IsEnabled = !_officeBusy && status.PayloadAvailable && !status.PowerPointRunning;
            OfficeConnectionText.Text = !status.PayloadAvailable ? "此版本缺少加载项组件，请使用完整发布版。" :
                status.PowerPointRunning ? await App.PowerPoint.GetOfficeAddinStatusAsync() :
                "PowerPoint 未运行；打开后刷新可查看实际加载状态。";
            if (_closed) return;
            if (status.PowerPointRunning && !_officeBusy)
            {
                OfficeOperationInfo.Severity = InfoBarSeverity.Informational;
                OfficeOperationInfo.Message = "PowerPoint 正在运行。关闭全部 PowerPoint 窗口后刷新，即可注册或卸载。";
                OfficeOperationInfo.IsOpen = true;
            }
        }
        catch (Exception ex)
        {
            if (!_closed) OfficeConnectionText.Text = "无法读取连接状态：" + ex.Message;
            CrashReporter.Report(ex, "office-addin-status");
        }
        finally { _officeRefreshing = false; }
    }

    private async void OnRefreshOfficeAddin(object sender, RoutedEventArgs args)
    {
        if (!_officeBusy) OfficeOperationInfo.IsOpen = false;
        await RefreshOfficeAddinAsync();
    }
    private async void OnRegisterOfficeAddin(object sender, RoutedEventArgs args) => await ManageOfficeAddinAsync(false);
    private async void OnUnregisterOfficeAddin(object sender, RoutedEventArgs args) => await ManageOfficeAddinAsync(true);

    private async Task ManageOfficeAddinAsync(bool uninstall)
    {
        if (_closed || _officeBusy) return;
        bool allUsers = OfficeScopeCombo.SelectedIndex == 1;
        _officeBusy = true;
        OfficeRegisterButton.IsEnabled = OfficeUnregisterButton.IsEnabled = OfficeScopeCombo.IsEnabled = false;
        OfficeOperationInfo.Severity = InfoBarSeverity.Informational;
        OfficeOperationInfo.Message = uninstall ? "正在卸载加载项…" : "正在注册加载项…";
        OfficeOperationInfo.IsOpen = true;
        try
        {
            await OfficeAddinService.RegisterAsync(allUsers, uninstall);
            if (_closed) return;
            OfficeOperationInfo.Severity = InfoBarSeverity.Success;
            OfficeOperationInfo.Message = uninstall ? "已移除此范围的注册。其他范围如有注册，可切换范围后继续卸载。" :
                "注册已完成。请重新打开 PowerPoint，然后刷新状态确认加载项已连接。";
        }
        catch (OperationCanceledException ex)
        {
            if (!_closed) { OfficeOperationInfo.Severity = InfoBarSeverity.Informational; OfficeOperationInfo.Message = ex.Message; }
        }
        catch (Exception ex)
        {
            CrashReporter.Report(ex, "office-addin-registration");
            if (!_closed) { OfficeOperationInfo.Severity = InfoBarSeverity.Error; OfficeOperationInfo.Message = ex.Message; }
        }
        finally
        {
            _officeBusy = false;
            if (!_closed) { OfficeScopeCombo.IsEnabled = true; await RefreshOfficeAddinAsync(); }
        }
    }
}
