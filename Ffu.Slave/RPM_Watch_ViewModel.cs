using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Ffu.Slave
{
    public sealed class WatchSlot : INotifyPropertyChanged
    {
        private int? _watchId;   // null 또는 1~64
        private string _currentRpm = "-";

        public int? WatchId
        {
            get => _watchId;
            set { if (_watchId == value) return; _watchId = value; OnPropertyChanged(); }
        }

        public string CurrentRpm
        {
            get => _currentRpm;
            set { if (_currentRpm == value) return; _currentRpm = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
