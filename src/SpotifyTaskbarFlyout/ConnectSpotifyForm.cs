using System.Diagnostics;

namespace SpotifyTaskbarFlyout;

/// <summary>
/// Clean modal dialog to configure a Spotify Client ID and complete the OAuth 2.0 PKCE login.
/// </summary>
internal sealed class ConnectSpotifyForm : Form
{
    private const string DashboardUrl = "https://developer.spotify.com/dashboard";

    private readonly SpotifyAuthService _auth;
    private readonly AppSettings _settings;
    private readonly TextBox _clientIdBox;
    private readonly Button _connectButton;
    private readonly Button _cancelButton;
    private readonly Label _statusLabel;

    private CancellationTokenSource? _loginCts;

    public ConnectSpotifyForm(SpotifyAuthService auth, AppSettings settings)
    {
        _auth = auth;
        _settings = settings;

        Theme theme = Theme.FromSystem();

        Text = "Connect Spotify Account";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(460, 340);
        BackColor = theme.Background;
        ForeColor = theme.Foreground;
        Font = new Font("Segoe UI", 9.5f);
        Icon = AppIcon.Load(SystemInformation.SmallIconSize);

        var title = new Label
        {
            Text = "Connect Spotify for in-app volume & playlists",
            Font = new Font("Segoe UI Semibold", 11.5f),
            Location = new Point(20, 18),
            Size = new Size(420, 26),
            ForeColor = theme.Foreground,
        };

        var steps = new Label
        {
            Text =
                "1. Open the Spotify Developer Dashboard and create an app.\n" +
                "2. Add this Redirect URI in your app settings:\n" +
                "3. Paste the Client ID below and click Connect.",
            Location = new Point(20, 50),
            Size = new Size(420, 58),
            ForeColor = theme.Secondary,
        };

        var redirectBox = new TextBox
        {
            Text = SpotifyAuthService.RedirectUri,
            ReadOnly = true,
            Location = new Point(20, 114),
            Size = new Size(330, 26),
            BackColor = theme.ControlBg,
            ForeColor = theme.Foreground,
            BorderStyle = BorderStyle.FixedSingle,
        };

        var copyBtn = new Button
        {
            Text = "Copy URI",
            Location = new Point(358, 113),
            Size = new Size(82, 27),
            FlatStyle = FlatStyle.Flat,
            BackColor = theme.ControlBg,
            ForeColor = theme.Foreground,
            Cursor = Cursors.Hand,
        };
        copyBtn.FlatAppearance.BorderColor = theme.Border;
        copyBtn.Click += (_, _) =>
        {
            Clipboard.SetText(SpotifyAuthService.RedirectUri);
            _statusLabel!.Text = "Redirect URI copied to clipboard.";
        };

        var dashboardLink = new LinkLabel
        {
            Text = "Open Spotify Developer Dashboard",
            Location = new Point(20, 148),
            Size = new Size(260, 22),
            LinkColor = theme.Accent,
            ActiveLinkColor = theme.AccentHover,
        };
        dashboardLink.LinkClicked += (_, _) =>
            Process.Start(new ProcessStartInfo(DashboardUrl) { UseShellExecute = true });

        var clientIdLabel = new Label
        {
            Text = "Spotify Client ID",
            Location = new Point(20, 182),
            Size = new Size(200, 20),
            ForeColor = theme.Foreground,
        };

        _clientIdBox = new TextBox
        {
            Text = _settings.SpotifyClientId,
            PlaceholderText = "e.g. 9f1c2e3b4a5d6e7f8a9b0c1d2e3f4a5b",
            Location = new Point(20, 205),
            Size = new Size(420, 27),
            BackColor = theme.ControlBg,
            ForeColor = theme.Foreground,
            BorderStyle = BorderStyle.FixedSingle,
        };

        _statusLabel = new Label
        {
            Text = string.Empty,
            Location = new Point(20, 244),
            Size = new Size(420, 32),
            ForeColor = theme.Secondary,
        };

        _connectButton = new Button
        {
            Text = "Connect with Spotify",
            Location = new Point(20, 286),
            Size = new Size(180, 34),
            FlatStyle = FlatStyle.Flat,
            BackColor = theme.Accent,
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 9.5f),
            Cursor = Cursors.Hand,
        };
        _connectButton.FlatAppearance.BorderSize = 0;
        _connectButton.Click += OnConnectClick;

        _cancelButton = new Button
        {
            Text = "Cancel",
            Location = new Point(340, 286),
            Size = new Size(100, 34),
            FlatStyle = FlatStyle.Flat,
            BackColor = theme.ControlBg,
            ForeColor = theme.Foreground,
            Cursor = Cursors.Hand,
        };
        _cancelButton.FlatAppearance.BorderColor = theme.Border;
        _cancelButton.Click += (_, _) =>
        {
            _loginCts?.Cancel();
            DialogResult = DialogResult.Cancel;
            Close();
        };

        Controls.AddRange(new Control[]
        {
            title, steps, redirectBox, copyBtn, dashboardLink,
            clientIdLabel, _clientIdBox, _statusLabel, _connectButton, _cancelButton,
        });

        AcceptButton = _connectButton;
        CancelButton = _cancelButton;
    }

    private async void OnConnectClick(object? sender, EventArgs e)
    {
        string clientId = _clientIdBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(clientId))
        {
            _statusLabel.Text = "Please enter your Spotify Client ID first.";
            _clientIdBox.Focus();
            return;
        }

        _connectButton.Enabled = false;
        _clientIdBox.Enabled = false;
        _statusLabel.Text = "Waiting for Spotify authorization in your browser...";

        _loginCts?.Cancel();
        _loginCts = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        try
        {
            bool ok = await _auth.LoginAsync(clientId, _loginCts.Token);
            if (ok)
            {
                DialogResult = DialogResult.OK;
                Close();
                return;
            }

            _statusLabel.Text = "Authorization was cancelled or failed. Please try again.";
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            _statusLabel.Text = $"Error: {ex.Message}";
        }
        finally
        {
            if (!IsDisposed)
            {
                _connectButton.Enabled = true;
                _clientIdBox.Enabled = true;
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _loginCts?.Cancel();
            _loginCts?.Dispose();
        }

        base.Dispose(disposing);
    }
}
