using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace CcDirector.Core.Configuration;

public class RepositoryConfig : INotifyPropertyChanged
{
    private int _uncommittedCount;

    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;

    [JsonIgnore]
    public int UncommittedCount
    {
        get => _uncommittedCount;
        set
        {
            if (_uncommittedCount == value) return;
            _uncommittedCount = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
