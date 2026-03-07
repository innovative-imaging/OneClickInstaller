namespace GVPPro;

public class ServicePasswordDialog : Form
{
    private TextBox txtPassword;
    private Button btnOk;
    private Button btnCancel;
    private Label lblPrompt;

    public string EnteredPassword => txtPassword.Text;

    public ServicePasswordDialog()
    {
        InitializeDialog();
    }

    private void InitializeDialog()
    {
        this.Text = "Service Authentication";
        this.Size = new Size(380, 180);
        this.FormBorderStyle = FormBorderStyle.FixedDialog;
        this.StartPosition = FormStartPosition.CenterParent;
        this.MaximizeBox = false;
        this.MinimizeBox = false;
        this.ShowInTaskbar = false;
        this.BackColor = Color.FromArgb(40, 40, 40);
        this.ForeColor = Color.White;

        lblPrompt = new Label
        {
            Text = "Enter service password:",
            Font = new Font("Segoe UI", 10F),
            ForeColor = Color.FromArgb(200, 200, 200),
            AutoSize = true,
            Location = new Point(20, 20)
        };

        txtPassword = new TextBox
        {
            Font = new Font("Segoe UI", 11F),
            UseSystemPasswordChar = true,
            Location = new Point(20, 50),
            Size = new Size(325, 30),
            BackColor = Color.FromArgb(60, 60, 60),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        txtPassword.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Enter) { DialogResult = DialogResult.OK; Close(); }
            if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
        };

        btnOk = new Button
        {
            Text = "OK",
            Font = new Font("Segoe UI", 9F),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(0, 100, 180),
            ForeColor = Color.White,
            Size = new Size(80, 32),
            Location = new Point(158, 95),
            DialogResult = DialogResult.OK
        };
        btnOk.FlatAppearance.BorderColor = Color.FromArgb(0, 120, 200);

        btnCancel = new Button
        {
            Text = "Cancel",
            Font = new Font("Segoe UI", 9F),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(60, 60, 60),
            ForeColor = Color.White,
            Size = new Size(80, 32),
            Location = new Point(246, 95),
            DialogResult = DialogResult.Cancel
        };
        btnCancel.FlatAppearance.BorderColor = Color.FromArgb(80, 80, 80);

        this.AcceptButton = btnOk;
        this.CancelButton = btnCancel;
        this.Controls.AddRange(new Control[] { lblPrompt, txtPassword, btnOk, btnCancel });
    }
}
