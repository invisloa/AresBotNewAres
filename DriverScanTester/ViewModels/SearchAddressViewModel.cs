using System;
using System.Windows.Input;
using System.Threading.Tasks;
using System.Windows;
using DriverScanTester.Utils;
using System.Runtime.InteropServices;

namespace DriverScanTester.ViewModels
{
    public class SearchAddressViewModel : BaseViewModel
    {
        private bool _isNumpadEnabled;
        private readonly MainViewModel _mainViewModel;

        public bool IsNumpadEnabled
        {
            get => _isNumpadEnabled;
            set => SetProperty(ref _isNumpadEnabled, value);
        }

        public bool IsByte
        {
            get => _mainViewModel.IsByte;
            set { _mainViewModel.IsByte = value; OnPropertyChanged(); }
        }

        public bool IsShort
        {
            get => _mainViewModel.IsShort;
            set { _mainViewModel.IsShort = value; OnPropertyChanged(); }
        }

        public bool IsCLong
        {
            get => _mainViewModel.IsCLong;
            set { _mainViewModel.IsCLong = value; OnPropertyChanged(); }
        }

        public bool IsLong
        {
            get => _mainViewModel.IsLong;
            set { _mainViewModel.IsLong = value; OnPropertyChanged(); }
        }

        public ICommand FirstScanCommand => _mainViewModel.FirstScanCommand;
        public ICommand IncreasedCommand => _mainViewModel.IncreasedCommand;
        public ICommand DecreasedCommand => _mainViewModel.DecreasedCommand;
        public ICommand SameAsOriginalCommand => _mainViewModel.SameAsOriginalCommand;
        public ICommand NotChangedCommand => _mainViewModel.NotChangedCommand;
        public ICommand ChangedCommand => _mainViewModel.ChangedCommand;
        public ICommand ShowMatchesCommand => _mainViewModel.ShowMatchesCommand;

        public ICommand CloseMatchesCommand { get; }

        public SearchAddressViewModel(MainViewModel mainViewModel)
        {
            _mainViewModel = mainViewModel;
            _mainViewModel.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(IsByte) || e.PropertyName == nameof(IsShort) || 
                    e.PropertyName == nameof(IsCLong) || e.PropertyName == nameof(IsLong))
                {
                    OnPropertyChanged(e.PropertyName);
                }
            };
            CloseMatchesCommand = new RelayCommand(_ => CloseMatches());
            StartNumpadListener();
        }

        private void CloseMatches()
        {
            foreach (Window window in System.Windows.Application.Current.Windows)
            {
                if (window.Title.StartsWith("Scan Results"))
                {
                    window.Close();
                    break;
                }
            }
        }

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private const int VK_NUMPAD8 = 0x68;
        private const int VK_NUMPAD2 = 0x62;
        private const int VK_NUMPAD5 = 0x65;
        private const int VK_NUMPAD4 = 0x64;
        private const int VK_NUMPAD6 = 0x66;
        private const int VK_NUMPAD0 = 0x60;
        private const int VK_NUMPAD7 = 0x67;
        private const int VK_NUMPAD9 = 0x69;
        private const int VK_NUMPAD1 = 0x61;

        private void StartNumpadListener()
        {
            Task.Run(async () =>
            {
                while (true)
                {
                    if (IsNumpadEnabled)
                    {
                        if ((GetAsyncKeyState(VK_NUMPAD1) & 0x8000) != 0)
                        {
                            ExecuteCommand(FirstScanCommand);
                            await WaitForKeyRelease(VK_NUMPAD1);
                        }
                        else if ((GetAsyncKeyState(VK_NUMPAD8) & 0x8000) != 0)
                        {
                            ExecuteCommand(IncreasedCommand);
                            await WaitForKeyRelease(VK_NUMPAD8);
                        }
                        else if ((GetAsyncKeyState(VK_NUMPAD2) & 0x8000) != 0)
                        {
                            ExecuteCommand(DecreasedCommand);
                            await WaitForKeyRelease(VK_NUMPAD2);
                        }
                        else if ((GetAsyncKeyState(VK_NUMPAD5) & 0x8000) != 0)
                        {
                            ExecuteCommand(NotChangedCommand);
                            await WaitForKeyRelease(VK_NUMPAD5);
                        }
                        else if ((GetAsyncKeyState(VK_NUMPAD4) & 0x8000) != 0 || (GetAsyncKeyState(VK_NUMPAD6) & 0x8000) != 0)
                        {
                            int key = (GetAsyncKeyState(VK_NUMPAD4) & 0x8000) != 0 ? VK_NUMPAD4 : VK_NUMPAD6;
                            ExecuteCommand(ChangedCommand);
                            await WaitForKeyRelease(key);
                        }
                        else if ((GetAsyncKeyState(VK_NUMPAD0) & 0x8000) != 0)
                        {
                            ExecuteCommand(SameAsOriginalCommand);
                            await WaitForKeyRelease(VK_NUMPAD0);
                        }
                        else if ((GetAsyncKeyState(VK_NUMPAD7) & 0x8000) != 0)
                        {
                            ExecuteCommand(ShowMatchesCommand);
                            await WaitForKeyRelease(VK_NUMPAD7);
                        }
                        else if ((GetAsyncKeyState(VK_NUMPAD9) & 0x8000) != 0)
                        {
                            ExecuteCommand(CloseMatchesCommand);
                            await WaitForKeyRelease(VK_NUMPAD9);
                        }
                    }
                    await Task.Delay(50);
                }
            });
        }

        private void ExecuteCommand(ICommand command)
        {
            if (command != null && command.CanExecute(null))
            {
                System.Windows.Application.Current?.Dispatcher?.Invoke(() => command.Execute(null));
            }
        }

        private async Task WaitForKeyRelease(int vKey)
        {
            while ((GetAsyncKeyState(vKey) & 0x8000) != 0)
            {
                await Task.Delay(50);
            }
        }
    }
}
