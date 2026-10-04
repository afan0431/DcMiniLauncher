using XIVLauncher.Account;
using XIVLauncher.Common.Game;
using XIVLauncher.Login.Client;
using XIVLauncher.Login.Models;

namespace XIVLauncher.Login.Workflow;

internal sealed class NewAccountDeviceProfileCoordinator
(
    AccountManager accountManager
)
{
    public DeviceProfilePreparation? Prepare(LoginWorkflowRequest request, ResolvedLoginState resolvedLoginState)
    {
        var requiresNewAccountDeviceProfileSetup    = request.RequireDeviceProfileSetupForNewLogin && resolvedLoginState.SavedAccount == null;
        var shouldRequestTemporaryQuickLoginSession = requiresNewAccountDeviceProfileSetup && resolvedLoginState.RequestedLoginType == LoginType.QRCode;
        var loginQuickLoginEnabled                  = resolvedLoginState.QuickLoginEnabled || shouldRequestTemporaryQuickLoginSession;

        if (resolvedLoginState.RequestedLoginType == LoginType.QRCode && request.RequireDeviceProfileSetupForQRCodeLogin)
            return PrepareForQRCodeLogin(request, resolvedLoginState, loginQuickLoginEnabled);

        if (!requiresNewAccountDeviceProfileSetup || resolvedLoginState.RequestedLoginType == LoginType.QRCode)
        {
            // 扫码时登录用户名被清空, 改用界面当前选中的账号决定本次用哪套设备, 免得已设独立设备的号被拿共享设备去扫
            var deviceProfileUsername = resolvedLoginState.RequestedLoginType == LoginType.QRCode ? request.Username : resolvedLoginState.Username;
            var resolvedDeviceProfile = accountManager.ResolveDeviceProfile(deviceProfileUsername, resolvedLoginState.AccountType);
            return new DeviceProfilePreparation
            (
                resolvedDeviceProfile,
                null,
                requiresNewAccountDeviceProfileSetup,
                loginQuickLoginEnabled
            );
        }

        var pendingNewAccount = CreatePendingNewAccount
        (
            resolvedLoginState.Username,
            resolvedLoginState.Username,
            resolvedLoginState.AccountType,
            resolvedLoginState.Area
        );

        switch (request.Interaction.PromptNewAccountDeviceProfileChoice())
        {
            case NewAccountDeviceProfileChoice.UseShared:
                return new DeviceProfilePreparation
                (
                    accountManager.ResolveDeviceProfile(pendingNewAccount),
                    pendingNewAccount,
                    true,
                    loginQuickLoginEnabled
                );

            case NewAccountDeviceProfileChoice.ConfigurePerAccount:
            {
                var configuredNewAccount = CreateIndependentDeviceProfileDraft(pendingNewAccount);

                if (!request.Interaction.ConfigureTemporaryAccountDeviceProfile(configuredNewAccount, accountManager))
                {
                    SavePendingNewAccountWithoutSecrets(pendingNewAccount);
                    return null;
                }

                return new DeviceProfilePreparation
                (
                    accountManager.ResolveDeviceProfile(configuredNewAccount),
                    configuredNewAccount,
                    true,
                    loginQuickLoginEnabled
                );
            }

            default:
                return null;
        }
    }

    private DeviceProfilePreparation? PrepareForQRCodeLogin(LoginWorkflowRequest request, ResolvedLoginState resolvedLoginState, bool loginQuickLoginEnabled)
    {
        var pendingNewAccount = CreatePendingNewAccount
        (
            resolvedLoginState.Username,
            resolvedLoginState.Username,
            resolvedLoginState.AccountType,
            resolvedLoginState.Area
        );

        switch (request.Interaction.PromptQRCodeDeviceProfileChoice())
        {
            case NewAccountDeviceProfileChoice.UseShared:
                return new DeviceProfilePreparation
                (
                    accountManager.ResolveDeviceProfile(pendingNewAccount),
                    pendingNewAccount,
                    false,
                    loginQuickLoginEnabled
                );

            case NewAccountDeviceProfileChoice.ConfigurePerAccount:
            {
                var configuredNewAccount = CreateIndependentDeviceProfileDraft(pendingNewAccount);

                if (!request.Interaction.ConfigureTemporaryAccountDeviceProfile(configuredNewAccount, accountManager))
                    return null;

                return new DeviceProfilePreparation
                (
                    accountManager.ResolveDeviceProfile(configuredNewAccount),
                    configuredNewAccount,
                    false,
                    loginQuickLoginEnabled
                );
            }

            default:
                return null;
        }
    }

    public PostQrDeviceProfileResult? ApplyAfterQrLogin(LoginWorkflowRequest request, DeviceProfilePreparation preparation, ResolvedLoginState resolvedLoginState, LoginResult loginResult)
    {
        // 扫到的是账号库里已有的号: 沿用它自己的设备设置, 不当成新号, 也不改回共享设备
        if (preparation.PendingNewAccount == null                  &&
            resolvedLoginState.RequestedLoginType == LoginType.QRCode &&
            loginResult is { State: LoginState.Ok, OAuthLogin: not null } &&
            accountManager.FindAccount(loginResult.OAuthLogin.InputUserID, resolvedLoginState.AccountType) is { } existingAccount)
            return new PostQrDeviceProfileResult(loginResult, accountManager.ResolveDeviceProfile(existingAccount), existingAccount);

        if (!preparation.RequiresNewAccountDeviceProfileSetup || resolvedLoginState.RequestedLoginType != LoginType.QRCode || loginResult.State != LoginState.Ok || loginResult.OAuthLogin == null)
            return new PostQrDeviceProfileResult(loginResult, preparation.ResolvedDeviceProfile, preparation.PendingNewAccount);

        var oAuthLogin = loginResult.OAuthLogin;
        var pendingNewAccount = preparation.PendingNewAccount
                                ?? CreatePendingNewAccount(oAuthLogin.InputUserID, oAuthLogin.SndaID, resolvedLoginState.AccountType, resolvedLoginState.Area);

        switch (request.Interaction.PromptNewAccountDeviceProfileChoice())
        {
            case NewAccountDeviceProfileChoice.UseShared:
                return new PostQrDeviceProfileResult(loginResult, accountManager.ResolveDeviceProfile(pendingNewAccount), pendingNewAccount);

            case NewAccountDeviceProfileChoice.ConfigurePerAccount:
            {
                var configuredNewAccount = CreateIndependentDeviceProfileDraft(pendingNewAccount);

                if (!request.Interaction.ConfigureTemporaryAccountDeviceProfile(configuredNewAccount, accountManager))
                {
                    SavePendingNewAccountWithoutSecrets(pendingNewAccount);
                    return null;
                }

                if (string.IsNullOrWhiteSpace(oAuthLogin.QuickLoginSecret))
                {
                    request.Interaction.ShowError("首次扫码登录未能获取可用于设备信息重登的会话密钥，本次无法继续启动游戏");
                    return null;
                }

                return new PostQrDeviceProfileResult(loginResult, accountManager.ResolveDeviceProfile(configuredNewAccount), configuredNewAccount);
            }

            default:
                return null;
        }
    }

    private static XIVAccount CreatePendingNewAccount(string loginAccount, string sndaId, XIVAccountType accountType, LoginArea area) =>
        new()
        {
            SdoLoginAccount             = loginAccount,
            WeGameLoginAccount          = loginAccount,
            AccountType                 = accountType,
            AreaName                    = area.AreaName,
            DeviceProfilePresetId       = string.Empty,
            DeviceProfileDynamicEnabled = false,
            IsDeviceProfileRotation     = true,
            DeviceProfileRotationDays   = AccountManager.DEFAULT_DEVICE_PROFILE_ROTATION_DAYS
        };

    private static XIVAccount CreateIndependentDeviceProfileDraft(XIVAccount account) =>
        new()
        {
            SdoLoginAccount                    = account.SdoLoginAccount,
            WeGameLoginAccount                 = account.WeGameLoginAccount,
            AccountType                        = account.AccountType,
            AreaName                           = account.AreaName,
            UserDefinedName                    = account.UserDefinedName,
            DeviceProfilePresetId              = account.DeviceProfilePresetId,
            DeviceProfileDynamicEnabled        = true,
            IsDeviceProfileRotation            = account.IsDeviceProfileRotation,
            DeviceProfileRotationDays          = account.DeviceProfileRotationDays,
            DeviceProfileLastGeneratedUtcTicks = account.DeviceProfileLastGeneratedUtcTicks
        };

    private void SavePendingNewAccountWithoutSecrets(XIVAccount account)
    {
        account.QuickLoginEnabled      = false;
        account.SdoQuickLoginSecret    = string.Empty;
        account.WeGameQuickLoginSecret = null;
        account.SdoPassword            = string.Empty;
        account.GenerateID();
        accountManager.AddAccount(account);
        accountManager.CurrentAccount = account;
        accountManager.Save();
    }
}

internal sealed record DeviceProfilePreparation
(
    ResolvedDeviceProfile ResolvedDeviceProfile,
    XIVAccount?           PendingNewAccount,
    bool                  RequiresNewAccountDeviceProfileSetup,
    bool                  LoginQuickLoginEnabled
);

internal sealed record PostQrDeviceProfileResult
(
    LoginResult           LoginResult,
    ResolvedDeviceProfile ResolvedDeviceProfile,
    XIVAccount?           PendingNewAccount
);
