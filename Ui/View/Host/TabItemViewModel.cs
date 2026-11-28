using System;
using System.ComponentModel;
using System.Diagnostics;
using Lemora.Model.Protocol.Base;
using Lemora.View.Host.ProtocolHosts;
using Shawn.Utils;
using Shawn.Utils.Interface;
using Shawn.Utils.Wpf;

namespace Lemora.View.Host
{
    public class TabItemViewModel : DisposableViewModel
    {
        public TabItemViewModel(HostBase hostBase, string displayName)
        {
            Content = hostBase;
            DisplayName = displayName;
            ColorHex = hostBase.ProtocolServer.ColorHex;
            IconImg = hostBase.ProtocolServer.IconImg;
            Content.ProtocolServer.PropertyChanged += ProtocolServerOnPropertyChanged;
            Lemora.Service.Mcp.McpGuard.ClaudeSessionsChanged += OnClaudeSessionsChanged;
            OnClaudeSessionsChanged();
        }

        private void OnClaudeSessionsChanged() => IsClaude = !_isDisposed && Lemora.Service.Mcp.McpGuard.IsClaude(Content.ConnectionId);

        private bool _isClaude;
        /// <summary>the session is driven by Claude: tab with the yellow line and the "AI" badge</summary>
        public bool IsClaude
        {
            get => _isClaude;
            private set { if (_isClaude == value) return; _isClaude = value; RaisePropertyChanged(); }
        }

        private void ProtocolServerOnPropertyChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (_isDisposed) return;

            if (args.PropertyName == nameof(ProtocolBase.DisplayName))
            {
                DisplayName = Content.ProtocolServer.DisplayName;
            }
        }

        public override void Dispose()
        {
            if (!_isDisposed)
            {
                Content.ProtocolServer.PropertyChanged -= ProtocolServerOnPropertyChanged;
                Lemora.Service.Mcp.McpGuard.ClaudeSessionsChanged -= OnClaudeSessionsChanged;
                _isDisposed = true;
            }
            base.Dispose();
        }


        public string DisplayName
        {
            get => _displayName;
            private set
            {
                _displayName = value;
                RaisePropertyChanged();
            }
        }

        public HostBase Content { get; }
        /// <summary>
        /// tab title mark color
        /// </summary>
        public string ColorHex { get; }
        public System.Windows.Media.Imaging.BitmapSource? IconImg { get; }



        private RelayCommand? _cmdReconnect;
        private string _displayName = "";

        public RelayCommand CmdReconnect
        {
            get
            {
                return _cmdReconnect ??= new RelayCommand((o) =>
                {
                    Content.ReConn();
                });
            }
        }
    }
}