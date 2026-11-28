using System;
using System.Threading.Tasks;
using Lemora.Utils;
using Lemora.View.Utils;
using Lemora.Utils.WindowsSdk;
using Lemora.Utils.WindowsApi.Credential;
using Lemora.Utils.WindowsSdk.PasswordVaultManager;
using Microsoft.Win32;

namespace Lemora.Service
{
    public static class SecondaryVerificationHelper
    {
        private static bool? _isEnabled = null;
        private static DateTime _lastVerifyTime = DateTime.MinValue;
        private const string _key = "SecondaryVerificationEnabled";

        /// <summary>
        /// By default the verification (Windows Hello or Windows password before showing or copying passwords) is ON. Whoever had turned it off or never set it finds it on
        /// only once (migration, marked in the user's registry); afterwards it can always be turned off from Settings and the choice stays.
        /// </summary>
        public static async void Init()
        {
            var ret = await GetEnabled(_key);
            bool migrated = false;
            try { migrated = Registry.CurrentUser.OpenSubKey($@"Software\{Assert.APP_NAME}")?.GetValue("SecondaryVerificationDefaultOn") != null; } catch { }
            if (ret == null || (ret == false && !migrated))
            {
                SetEnabled(true);
            }
            if (!migrated)
            {
                try { Registry.CurrentUser.CreateSubKey($@"Software\{Assert.APP_NAME}")?.SetValue("SecondaryVerificationDefaultOn", "1"); } catch { }
            }
        }


        public static async void SetEnabled(bool enable)
        {
            var success = await SetEnabled(enable, _key);
            if (success)
                _isEnabled = enable;
        }


        /// <summary>automated tests only: turns off the verification ONLY in memory. Tests must never write the real setting (it lives in the Windows Credential Manager, shared by all copies of the app for the same user)</summary>
        public static bool? TestOverride { get; set; }

        /// <summary>automated tests only, in memory: fake verification result (null = real verification) and how many times it was requested</summary>
        public static bool? TestVerifyResult { get; set; }
        public static int TestVerifyCalls { get; set; }

        public static async Task<bool> GetEnabled()
        {
            if (TestOverride != null) return TestOverride.Value;
            if (_isEnabled != null) return (bool)_isEnabled;
            var ret = await GetEnabled(_key);
            _isEnabled = ret != false;
            return (bool)_isEnabled;
        }


        /// <summary>brings the main window to the front (if it was in the tray or minimized) and returns its handle, to attach Windows Hello to it</summary>
        private static IntPtr FrontMainWindow()
        {
            try
            {
                return System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    if (IoC.Get<Lemora.View.MainWindowViewModel>().View is not System.Windows.Window w) return IntPtr.Zero;
                    if (w.WindowState == System.Windows.WindowState.Minimized) w.WindowState = System.Windows.WindowState.Normal;
                    if (!w.IsVisible) w.Show();
                    w.Activate();
                    return new System.Windows.Interop.WindowInteropHelper(w).Handle;
                });
            }
            catch { return IntPtr.Zero; }
        }

        public static async Task<bool?> VerifyAsyncUi(bool defaultReturn = false, bool returnUntilOkOrCancel = true)
        {
            if (await GetEnabled() == false)
                return true;
            if (TestVerifyResult is { } fake) { TestVerifyCalls++; return fake; }   // automated tests only (Hello is not used in a hidden instance)

            //if ((DateTime.Now - _lastVerifyTime).TotalSeconds < 30)
            //    return true;

            bool? result;
            var owner = FrontMainWindow();   // the verification window attaches to Lemora, brought to the front: no more prompt hidden underneath
            bool widowsHelloIsOk = await WindowsHelloHelper.HelloIsAvailable() == true;
            MaskLayerController.ShowProcessingRing(IoC.Translate("Please complete the windows credentials verification"));
            string title = Assert.APP_DISPLAY_NAME + ": " + IoC.Translate("Enter your credentials");
            string message = IoC.Translate("Before proceeding with sensitive operations, we need to make sure it is you.");
            while (true)
            {
                if (!widowsHelloIsOk)
                {
                    try
                    {
                        var ret = CredentialPrompt.LogonUserWithWindowsCredential(title, message,
                            owner != IntPtr.Zero ? owner : (IntPtr?)null,
                            null, null,
                            0
                            | (uint)CredentialPrompt.PromptForWindowsCredentialsFlag.CREDUIWIN_GENERIC
                            | (uint)CredentialPrompt.PromptForWindowsCredentialsFlag.CREDUIWIN_ENUMERATE_CURRENT_USER
                        );
                        if (ret == CredentialPrompt.LogonUserStatus.Success)
                        {
                            result = true;
                        }
                        else if (ret == CredentialPrompt.LogonUserStatus.Cancel)
                        {
                            result = null;
                        }
                        else
                        {
                            result = defaultReturn;
                        }
                    }
                    catch (Exception)
                    {
                        result = defaultReturn;
                    }
                }
                else
                {
                    result = await WindowsHelloHelper.HelloVerifyAsync($"{title}\r\n{message}", owner);
                }

                if (result != false)
                    break;
                if (returnUntilOkOrCancel == false)
                    break;

                message = IoC.Translate("Verification failed. Please try again.");
            }

            MaskLayerController.HideMask();
            if (result == true)
                _lastVerifyTime = DateTime.Now;
            return result;
        }


        public static void VerifyAsyncUiCallBack(Action<bool?> callBack, bool defaultReturn = false, bool returnUntilOkOrCancel = true)
        {
            Task.Factory.StartNew(async () =>
            {
                bool? result = await VerifyAsyncUi(defaultReturn, returnUntilOkOrCancel);
                callBack.Invoke(result);
            });
        }





        private static async Task<bool> SetEnabled(bool enable, string key)
        {
            var success = false;
            var value = await DataProtectionForLocal.Protect("0") ?? "";

            if (enable == false)
            {
                string password = "";
                string username = "";
                if (value.Length > 1000)
                {
                    password = DataVault.EncryptOnce("0");
                }
                else if (value.Length > 500)
                {
                    password = value.Substring(0, 500);
                    username = value.Substring(500);
                }
                success = Credential.Set($@"{Assert.APP_NAME}\{key}", username, password);
            }
            else
            {
                success = Credential.Set($@"{Assert.APP_NAME}\{key}", "", "");
            }


            if (!success || enable == true)
            {
                try
                {
                    var openSubKey = Registry.CurrentUser.OpenSubKey("Software", true);
                    var appNameKey = openSubKey?.CreateSubKey(Assert.APP_NAME);
                    if (appNameKey != null)
                    {
                        appNameKey.SetValue(key, enable == false ? value : "");
                        success = true;
                    }
                }
                catch (Exception)
                {
                    success = false;
                }
            }

            if (!success || enable == true)
            {
                try
                {
                    var pvm = new PasswordVaultManagerFileSystem(AppPathHelper.Instance.LocalityDirPath);
                    if (enable == false)
                    {
                        pvm.Add(key, value);
                        success = true;
                    }
                    else
                    {
                        pvm.Remove(key);
                    }
                }
                catch (Exception)
                {
                    success = false;
                }
            }
            return success;
        }
        private static async Task<bool?> GetEnabled(string key)
        {
            try
            {
                var c = Credential.Load($@"{Assert.APP_NAME}\{key}");
                if (c != null)
                {
                    if (await DataProtectionForLocal.Unprotect(c.Password + c.Username) == "0")
                    {
                        return false;
                    }
                    if (DataVault.SimpleDecrypt(c.Password + c.Username) == "0")
                    {
                        return false;
                    }
                    return true;
                }
            }
            catch (Exception)
            {
                // ignored
            }

            try
            {
                var openSubKey = Registry.CurrentUser.OpenSubKey("Software", true);
                var appNameKey = openSubKey?.CreateSubKey(Assert.APP_NAME);
                if (appNameKey?.GetValue(key) is string txt)
                {
                    var value = await DataProtectionForLocal.Unprotect(txt);
                    return value != "0";
                }
            }
            catch (Exception)
            {
                // ignored
            }


            try
            {
                var pvm = new PasswordVaultManagerFileSystem(AppPathHelper.Instance.LocalityDirPath);
                if (pvm.Retrieve(key) is { } txt)
                {
                    var value = await DataProtectionForLocal.Unprotect(txt);
                    return value != "0";
                }
            }
            catch (Exception)
            {
                // ignored
            }

            return null;
        }

    }
}
