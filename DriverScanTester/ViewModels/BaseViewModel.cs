using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DriverScanTester.ViewModels
{
    /// <summary>
    /// Minimal base implementation that exposes <see cref="INotifyPropertyChanged"/> helpers and a couple of
    /// convenience properties shared by the view-models.
    /// </summary>
    public abstract class BaseViewModel : INotifyPropertyChanged
    {
        /// <inheritdoc />
        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        /// Raises <see cref="PropertyChanged"/> for the provided property name.
        /// </summary>
        /// <param name="propertyName">The name of the property that changed.</param>
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>
        /// Helper that updates the backing field and triggers <see cref="PropertyChanged"/> when the value actually changes.
        /// </summary>
        /// <typeparam name="T">Type of the property.</typeparam>
        /// <param name="storage">Reference to the backing field.</param>
        /// <param name="value">Value to assign.</param>
        /// <param name="propertyName">Automatically provided property name.</param>
        /// <returns><c>true</c> when the value was changed; otherwise <c>false</c>.</returns>
        protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string propertyName = null)
        {
            if (Equals(storage, value))
            {
                return false;
            }

            storage = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        private bool _isBusy;

        /// <summary>
        /// Indicates whether the view-model is busy performing an asynchronous operation.
        /// </summary>
        public bool IsBusy
        {
            get => _isBusy;
            set => SetProperty(ref _isBusy, value);
        }

        private string _title = string.Empty;

        /// <summary>
        /// Optional title for the view-model.
        /// </summary>
        public string Title
        {
            get => _title;
            set => SetProperty(ref _title, value);
        }
    }
}
