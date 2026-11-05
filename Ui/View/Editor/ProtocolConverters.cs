using System;
using System.Globalization;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Data;

namespace Lemora.View.Editor
{
    /// <summary>nome del protocollo -> titolo del gruppo nella colonna sinistra dell'editor</summary>
    public class ProtocolGroupConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var key = (value as string) switch
            {
                "RDP" or "RemoteApp" or "VNC" => "editor_group_remote",
                "SSH" or "Telnet" or "Serial" => "editor_group_terminal",
                "SFTP" or "FTP" => "editor_group_files",
                _ => "editor_group_local",
            };
            return IoC.Translate(key);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    /// <summary>nome del protocollo -> descrizione breve tradotta</summary>
    public class ProtocolDescriptionConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var name = value as string;
            if (string.IsNullOrEmpty(name)) return "";
            var key = "protocol_desc_" + name;
            var text = IoC.Translate(key);
            return text == key ? "" : text;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    /// <summary>nome del protocollo -> testo di aiuto tradotto (chiave = parametro + nome)</summary>
    public class ProtocolTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var name = value as string;
            if (string.IsNullOrEmpty(name)) return "";
            var key = parameter + name;
            var text = IoC.Translate(key);
            return text == key ? "" : text;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    /// <summary>true dopo un tentativo di salvataggio con dati obbligatori mancanti: evidenzia i campi in errore</summary>
    public class EditorUiState : INotifyPropertyChanged
    {
        public static EditorUiState Instance { get; } = new EditorUiState();
        private bool _highlight;
        public bool Highlight
        {
            get => _highlight;
            set
            {
                if (_highlight == value) return;
                _highlight = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Highlight)));
            }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>riassunto a una riga e pallino "modificato" per le sezioni avanzate (Expander)</summary>
    public static class AccordionHelper
    {
        public static readonly DependencyProperty SummaryProperty = DependencyProperty.RegisterAttached("Summary", typeof(string), typeof(AccordionHelper), new PropertyMetadata(""));
        public static string GetSummary(DependencyObject o) => (string)o.GetValue(SummaryProperty);
        public static void SetSummary(DependencyObject o, string v) => o.SetValue(SummaryProperty, v);

        public static readonly DependencyProperty IsModifiedProperty = DependencyProperty.RegisterAttached("IsModified", typeof(bool), typeof(AccordionHelper), new PropertyMetadata(false));
        public static bool GetIsModified(DependencyObject o) => (bool)o.GetValue(IsModifiedProperty);
        public static void SetIsModified(DependencyObject o, bool v) => o.SetValue(IsModifiedProperty, v);

        /// <summary>nomi (separati da virgola) dei campi della sezione: il riassunto e il pallino si calcolano confrontandoli con i predefiniti</summary>
        public static readonly DependencyProperty PropertiesProperty = DependencyProperty.RegisterAttached("Properties", typeof(string), typeof(AccordionHelper), new PropertyMetadata("", OnPropertiesChanged));
        public static string GetProperties(DependencyObject o) => (string)o.GetValue(PropertiesProperty);
        public static void SetProperties(DependencyObject o, string v) => o.SetValue(PropertiesProperty, v);

        private sealed class Hook
        {
            public INotifyPropertyChanged? Target;
            public PropertyChangedEventHandler? Handler;
        }
        private static readonly DependencyProperty HookProperty = DependencyProperty.RegisterAttached("Hook", typeof(Hook), typeof(AccordionHelper));
        private static readonly Dictionary<Type, object?> Defaults = new Dictionary<Type, object?>();

        private static void OnPropertiesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not FrameworkElement fe || string.IsNullOrEmpty(e.NewValue as string)) return;
            fe.Loaded += (_, _) => Attach(fe);
            fe.DataContextChanged += (_, _) => Attach(fe);
        }

        // proprieta' piu' derivata con quel nome (i form dichiarano "new RDP New" che nasconde quella della base: GetProperty darebbe errore)
        private static System.Reflection.PropertyInfo? Prop(Type type, string name)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                var pi = t.GetProperty(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly);
                if (pi != null) return pi;
            }
            return null;
        }

        // il campo da osservare e' "New" nei form dei protocolli, "Server" nell'editor principale
        private static INotifyPropertyChanged? FindTarget(FrameworkElement fe)
        {
            var dc = fe.DataContext;
            if (dc == null) return null;
            foreach (var name in new[] { "New", "Server" })
                if (Prop(dc.GetType(), name)?.GetValue(dc) is INotifyPropertyChanged t) return t;
            return null;
        }

        private static void Attach(FrameworkElement fe)
        {
            var old = (Hook?)fe.GetValue(HookProperty);
            if (old?.Target != null && old.Handler != null) old.Target.PropertyChanged -= old.Handler;
            var target = FindTarget(fe);
            if (target == null) return;
            var names = GetProperties(fe).Split(',', StringSplitOptions.RemoveEmptyEntries);
            var hook = new Hook { Target = target };
            hook.Handler = (_, args) =>
            {
                if (string.IsNullOrEmpty(args.PropertyName) || names.Contains(args.PropertyName)) Update(fe, target, names);
            };
            target.PropertyChanged += hook.Handler;
            fe.SetValue(HookProperty, hook);
            Update(fe, target, names);
        }

        private static string Norm(object? v) => v switch
        {
            null => "",
            string s => s,
            IEnumerable list => string.Join("|", list.Cast<object?>().Select(x => x?.ToString())),
            _ => v.ToString() ?? "",
        };

        private static void Update(FrameworkElement fe, object target, string[] names)
        {
            var type = target.GetType();
            if (!Defaults.TryGetValue(type, out var def))
            {
                try { def = Activator.CreateInstance(type); } catch { def = null; }
                Defaults[type] = def;
            }
            var n = 0;
            foreach (var name in names)
            {
                var pi = Prop(type, name);
                if (pi == null || def == null) continue;
                if (Norm(pi.GetValue(target)) != Norm(pi.GetValue(def))) n++;
            }
            fe.Dispatcher.BeginInvoke(new Action(() =>
            {
                SetIsModified(fe, n > 0);
                SetSummary(fe, n == 0 ? IoC.Translate("editor_advanced_default") : string.Format(IoC.Translate(n == 1 ? "editor_advanced_one" : "editor_advanced_n"), n));
            }));
        }
    }

    /// <summary>"" (segui le impostazioni globali) <-> voce "Follow the global settings" della lista dei client</summary>
    public class RunnerNameConverter : IValueConverter
    {
        public const string Follow = "Follow the global settings";
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => string.IsNullOrEmpty(value as string) ? Follow : value;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value ?? "";
    }

    /// <summary>nome del client -> testo mostrato (traduce solo la voce "segui le impostazioni globali")</summary>
    public class RunnerDisplayConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var name = value as string ?? "";
            return name == RunnerNameConverter.Follow ? IoC.Translate("editor_runner_follow") : name;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
