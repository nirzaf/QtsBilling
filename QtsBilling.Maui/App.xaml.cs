namespace QtsBilling.Maui;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        return new Window(new MainPage())
        {
            Title = "QTS Billing",
            Width = 1280,
            Height = 800,
        };
    }
}
