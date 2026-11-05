using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using Lemora.Model;
using Lemora.Model.Protocol;
using Lemora.Service.DataSource;
using Lemora.Utils;
using Lemora.Utils.Tracing;
using Lemora.View;
using Lemora.View.Launcher;
using Newtonsoft.Json;
using Shawn.Utils;

namespace Lemora.Service.Locality
{
    public enum EnumServerOrderBy
    {
        IdAsc = -1,
        ProtocolAsc = 0,
        ProtocolDesc = 1,
        NameAsc = 2,
        NameDesc = 3,

        //TagAsc = 4,
        //TagDesc = 5,
        AddressAsc = 6,
        AddressDesc = 7,
        Custom = 999,
    }

    internal class LocalitySettings
    {
        public double MainWindowTop = double.NaN;
        public double MainWindowLeft = double.NaN;
        public double MainWindowWidth = 1180;
        public double MainWindowHeight = 740;
        public WindowState MainWindowState = WindowState.Normal;
        public double TabWindowTop = -1;
        public double TabWindowLeft = -1;
        public double TabWindowWidth = 800;
        public double TabWindowHeight = 600;
        public WindowState TabWindowState = WindowState.Normal;
        public WindowStyle TabWindowStyle = WindowStyle.SingleBorderWindow;
        public int FtpColumnFileNameLength = -1;
        public int FtpColumnFileTimeLength = -1;
        public int FtpColumnFileTypeLength = -1;
        public int FtpColumnFileSizeLength = -1;
        public Dictionary<string, string> Misc = new Dictionary<string, string>();
    }

    public sealed class LocalityService
    {
        private readonly LocalitySettings _localitySettings;
        public static string JsonPath => Path.Combine(AppPathHelper.Instance.LocalityDirPath, ".locality.json");
        private const string DbKey = "window_settings";
        public bool CanSave = true;

        // Trascinando o ridimensionando una finestra posizione e grandezza cambiano a ogni movimento del mouse: salvarle ogni volta
        // (cifratura ~20 ms + scrittura nel database, fino a 4 volte per passo con l'angolo in alto a sinistra) faceva andare a scatti la finestra.
        // Si salva una volta sola, mezzo secondo dopo l'ultima modifica; alla chiusura dell'app si salva subito quello che manca (Flush).
        private System.Threading.Timer? _saveTimer;
        private bool _savePending;

        private void Save()
        {
            if (!CanSave) return;
            lock (this)
            {
                _savePending = true;
                _saveTimer ??= new System.Threading.Timer(_ => Flush());
                _saveTimer.Change(500, System.Threading.Timeout.Infinite);
            }
        }

        /// <summary>salva subito le modifiche ancora in attesa (chiamata dal timer e alla chiusura dell'app)</summary>
        public void Flush()
        {
            lock (this)
            {
                if (!_savePending || !CanSave) return;
                _savePending = false;
                CanSave = false;
                RetryHelper.Try(() => { AppDbStore.Set(DbKey, JsonConvert.SerializeObject(this._localitySettings)); },
                    actionOnError: exception => UnifyTracing.Error(exception));
                CanSave = true;
            }
        }

        public LocalityService()
        {
            // Load
            _localitySettings = new LocalitySettings();
            try
            {
                // dal database (importando il vecchio file la prima volta); se il database non e' ancora pronto si legge il vecchio file
                var json = AppDbStore.ReadOrMigrate(DbKey, JsonPath, out var ready);
                if (!ready && File.Exists(JsonPath)) json = File.ReadAllText(JsonPath);
                var tmp = string.IsNullOrEmpty(json) ? null : JsonConvert.DeserializeObject<LocalitySettings>(json);
                if (tmp != null)
                    _localitySettings = tmp;
            }
            catch
            {
                // ignored
            }
        }


        public double MainWindowTop
        {
            get => _localitySettings.MainWindowTop;
            set
            {
                if (double.IsNaN(_localitySettings.MainWindowTop) || Math.Abs(_localitySettings.MainWindowTop - value) > 0.001)
                {
                    _localitySettings.MainWindowTop = value;
                    Save();
                }
            }
        }

        public double MainWindowLeft
        {
            get => _localitySettings.MainWindowLeft;
            set
            {
                if (double.IsNaN(_localitySettings.MainWindowLeft) || Math.Abs(_localitySettings.MainWindowLeft - value) > 0.001)
                {
                    _localitySettings.MainWindowLeft = value;
                    Save();
                }
            }
        }

        public double MainWindowWidth
        {
            get => _localitySettings.MainWindowWidth;
            set
            {
                if (Math.Abs(_localitySettings.MainWindowWidth - value) > 0.001)
                {
                    _localitySettings.MainWindowWidth = value;
                    Save();
                }
            }
        }

        public double MainWindowHeight
        {
            get => _localitySettings.MainWindowHeight;
            set
            {
                if (Math.Abs(_localitySettings.MainWindowHeight - value) > 0.001)
                {
                    _localitySettings.MainWindowHeight = value;
                    Save();
                }
            }
        }

        public WindowState MainWindowState
        {
            get => _localitySettings.MainWindowState;
            set
            {
                if (_localitySettings.MainWindowState != value)
                {
                    _localitySettings.MainWindowState = value;
                    Save();
                }
            }
        }

        public double TabWindowTop
        {
            get => _localitySettings.TabWindowTop;
            set
            {
                if (Math.Abs(_localitySettings.TabWindowTop - value) > 0.001)
                {
                    _localitySettings.TabWindowTop = value;
                    Save();
                }
            }
        }

        public double TabWindowLeft
        {
            get => _localitySettings.TabWindowLeft;
            set
            {
                if (Math.Abs(_localitySettings.TabWindowLeft - value) > 0.001)
                {
                    _localitySettings.TabWindowLeft = value;
                    Save();
                }
            }
        }

        public double TabWindowWidth
        {
            get => _localitySettings.TabWindowWidth;
            set
            {
                if (Math.Abs(_localitySettings.TabWindowWidth - value) > 0.001)
                {
                    _localitySettings.TabWindowWidth = value;
                    Save();
                }
            }
        }

        public double TabWindowHeight
        {
            get => _localitySettings.TabWindowHeight;
            set
            {
                if (Math.Abs(_localitySettings.TabWindowHeight - value) > 0.001)
                {
                    _localitySettings.TabWindowHeight = value;
                    Save();
                }
            }
        }

        public WindowState TabWindowState
        {
            get => _localitySettings.TabWindowState;
            set
            {
                if (_localitySettings.TabWindowState != value)
                {
                    _localitySettings.TabWindowState = value;
                    Save();
                }
            }
        }

        public WindowStyle TabWindowStyle
        {
            get => _localitySettings.TabWindowStyle;
            set
            {
                if (_localitySettings.TabWindowStyle != value)
                {
                    _localitySettings.TabWindowStyle = value;
                    Save();
                }
            }
        }

        public int FtpColumnFileNameLength
        {
            get => _localitySettings.FtpColumnFileNameLength;
            set
            {
                if (_localitySettings.FtpColumnFileNameLength != value)
                {
                    _localitySettings.FtpColumnFileNameLength = value;
                    Save();
                }
            }
        }

        public int FtpColumnFileTimeLength
        {
            get => _localitySettings.FtpColumnFileTimeLength;
            set
            {
                if (_localitySettings.FtpColumnFileTimeLength != value)
                {
                    _localitySettings.FtpColumnFileTimeLength = value;
                    Save();
                }
            }
        }

        public int FtpColumnFileTypeLength
        {
            get => _localitySettings.FtpColumnFileTypeLength;
            set
            {
                if (_localitySettings.FtpColumnFileTypeLength != value)
                {
                    _localitySettings.FtpColumnFileTypeLength = value;
                    Save();
                }
            }
        }
        public int FtpColumnFileSizeLength
        {
            get => _localitySettings.FtpColumnFileSizeLength;
            set
            {
                if (_localitySettings.FtpColumnFileSizeLength != value)
                {
                    _localitySettings.FtpColumnFileSizeLength = value;
                    Save();
                }
            }
        }

        public void SetMisc(string key, string value)
        {
            if (_localitySettings.Misc.ContainsKey(key))
            {
                if (_localitySettings.Misc[key] == value) return;
                _localitySettings.Misc[key] = value;
            }
            else
            {
                _localitySettings.Misc.Add(key, value);
            }
            Save();
        }

        
        public T GetMisc<T>(string key, T defaultValue = default!)
        {
            var value = _localitySettings.Misc.ContainsKey(key) ? _localitySettings.Misc[key] : "";
            if (typeof(T) == typeof(int))
            {
                return int.TryParse(value, out var result) ? (T)(object)result : defaultValue;
            }
            if (typeof(T) == typeof(bool))
            {
                return bool.TryParse(value, out var result) ? (T)(object)result : defaultValue;
            }
            if (typeof(T) == typeof(float))
            {
                return float.TryParse(value, out var result) ? (T)(object)result : defaultValue;
            }
            if (typeof(T) == typeof(double))
            {
                return double.TryParse(value, out var result) ? (T)(object)result : defaultValue;
            }
            if (typeof(T) == typeof(string))
            {
                return (T)(object)value;
            }
            throw new NotSupportedException($"Not support type {typeof(T)}");
        }
    }
}