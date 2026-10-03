using XIVLauncher.Account;
using XIVLauncher.Account.DeviceProfiles;

namespace XIVLauncher.CatHost;

/// <summary>
///     无界面启动取设备: 只读账号库里这个号已存的设备, 不轮换、不改任何设备设置
/// </summary>
public static class CatDeviceProfiles
{
    /// <summary>
    ///     取账号当前使用的设备。账号开了独立设备但预设丢失时返回 null（不能退回共享设备, 那会换设备登录）。
    /// </summary>
    /// <param name="accountManager">账号库</param>
    /// <param name="account">账号</param>
    /// <param name="isPerAccount">是否为账号独立设备</param>
    public static DeviceProfileSnapshot? Resolve(AccountManager accountManager, XIVAccount account, out bool isPerAccount)
    {
        isPerAccount = account.DeviceProfileDynamicEnabled;

        if (!account.DeviceProfileDynamicEnabled)
            return accountManager.GetSharedDeviceProfilePreset().ToSnapshot();

        if (accountManager.FindDeviceProfilePreset(account.DeviceProfilePresetId) is { } preset)
            return preset.ToSnapshot();

        if (!string.IsNullOrWhiteSpace(account.DeviceProfileDeviceId)   &&
            !string.IsNullOrWhiteSpace(account.DeviceProfileMacAddress) &&
            !string.IsNullOrWhiteSpace(account.DeviceProfileHostName))
        {
            return new DeviceProfileSnapshot
            {
                DeviceId   = account.DeviceProfileDeviceId,
                MacAddress = account.DeviceProfileMacAddress,
                HostName   = account.DeviceProfileHostName
            };
        }

        return null;
    }
}
