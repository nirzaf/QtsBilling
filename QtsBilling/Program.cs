using QtsBilling.UIForms;

namespace QtsBilling;

public static class Program
{
    public static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new Customers());
    }
}