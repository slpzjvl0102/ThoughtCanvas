using CommunityToolkit.Mvvm.ComponentModel;

namespace ThoughtCanvas;
public sealed class ShellViewModel : ObservableObject
{
    private string status = "준비 중";
    public string Status { get => status; set => SetProperty(ref status, value); }
}
