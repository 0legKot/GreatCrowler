using System;

namespace GreatCrowler;

public partial class MainPage : ContentPage
{
    public MainPage()
	{
		InitializeComponent();
	}

	private async void OnGetEmailsClicked(object sender, EventArgs e)
	{
        SearchProgress.Progress = 0;
        Emails.Text = "";
        if (Domains.Text is null) { return; }
        var emails = await Task.Run(() => EmailSearcher.Search(Domains.Text.Split("\r"), progress => Dispatcher.Dispatch(() => SearchProgress.Progress = progress)));
		Emails.Text = string.Join(Environment.NewLine, emails);
	}
}
