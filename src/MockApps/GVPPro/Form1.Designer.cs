namespace GVPPro;

partial class Form1
{
    /// <summary>
    ///  Required designer variable.
    /// </summary>
    private System.ComponentModel.IContainer components = null;

    /// <summary>
    ///  Clean up any resources being used.
    /// </summary>
    /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing && (components != null))
        {
            components.Dispose();
        }
        base.Dispose(disposing);
    }

    #region Windows Form Designer generated code

    /// <summary>
    ///  Required method for Designer support - do not modify
    ///  the contents of this method with the code editor.
    /// </summary>
    private void InitializeComponent()
    {
        this.components = new System.ComponentModel.Container();
        this.lblTitle = new Label();
        this.lblVersion = new Label();
        this.lblStatus = new Label();
        this.btnService = new Button();
        this.btnLogOff = new Button();
        this.pnlHeader = new Panel();
        this.pnlFooter = new Panel();
        this.pnlCenter = new Panel();
        this.lblCenterMessage = new Label();

        // 
        // pnlHeader
        // 
        this.pnlHeader.BackColor = Color.FromArgb(30, 30, 30);
        this.pnlHeader.Dock = DockStyle.Top;
        this.pnlHeader.Height = 60;
        this.pnlHeader.Controls.Add(this.lblTitle);
        this.pnlHeader.Controls.Add(this.lblStatus);

        // 
        // lblTitle
        // 
        this.lblTitle.Text = "GVP-Pro";
        this.lblTitle.Font = new Font("Segoe UI", 20F, FontStyle.Bold);
        this.lblTitle.ForeColor = Color.White;
        this.lblTitle.AutoSize = true;
        this.lblTitle.Location = new Point(20, 12);

        // 
        // lblStatus
        // 
        this.lblStatus.Text = "● System Ready";
        this.lblStatus.Font = new Font("Segoe UI", 11F);
        this.lblStatus.ForeColor = Color.FromArgb(0, 200, 83);
        this.lblStatus.AutoSize = true;
        this.lblStatus.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        this.lblStatus.Location = new Point(600, 20);

        // 
        // pnlCenter
        // 
        this.pnlCenter.BackColor = Color.FromArgb(20, 20, 25);
        this.pnlCenter.Dock = DockStyle.Fill;
        this.pnlCenter.Controls.Add(this.lblCenterMessage);

        // 
        // lblCenterMessage
        // 
        this.lblCenterMessage.Text = "Medical Imaging System\nAwaiting Patient...";
        this.lblCenterMessage.Font = new Font("Segoe UI", 16F);
        this.lblCenterMessage.ForeColor = Color.FromArgb(120, 120, 140);
        this.lblCenterMessage.TextAlign = ContentAlignment.MiddleCenter;
        this.lblCenterMessage.Dock = DockStyle.Fill;

        // 
        // pnlFooter
        // 
        this.pnlFooter.BackColor = Color.FromArgb(30, 30, 30);
        this.pnlFooter.Dock = DockStyle.Bottom;
        this.pnlFooter.Height = 50;
        this.pnlFooter.Controls.Add(this.lblVersion);
        this.pnlFooter.Controls.Add(this.btnService);
        this.pnlFooter.Controls.Add(this.btnLogOff);

        // 
        // lblVersion
        // 
        this.lblVersion.Text = "v1.0.2";
        this.lblVersion.Font = new Font("Segoe UI", 9F);
        this.lblVersion.ForeColor = Color.FromArgb(100, 100, 100);
        this.lblVersion.AutoSize = true;
        this.lblVersion.Location = new Point(20, 16);

        // 
        // btnLogOff
        // 
        this.btnLogOff.Text = "Log Off";
        this.btnLogOff.Font = new Font("Segoe UI", 9F);
        this.btnLogOff.FlatStyle = FlatStyle.Flat;
        this.btnLogOff.FlatAppearance.BorderColor = Color.FromArgb(80, 80, 80);
        this.btnLogOff.BackColor = Color.FromArgb(50, 50, 50);
        this.btnLogOff.ForeColor = Color.White;
        this.btnLogOff.Size = new Size(90, 34);
        this.btnLogOff.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        this.btnLogOff.Location = new Point(594, 8);
        this.btnLogOff.Cursor = Cursors.Hand;

        // 
        // btnService
        // 
        this.btnService.Text = "Service";
        this.btnService.Font = new Font("Segoe UI", 9F);
        this.btnService.FlatStyle = FlatStyle.Flat;
        this.btnService.FlatAppearance.BorderColor = Color.FromArgb(200, 150, 0);
        this.btnService.BackColor = Color.FromArgb(60, 50, 20);
        this.btnService.ForeColor = Color.FromArgb(255, 200, 50);
        this.btnService.Size = new Size(90, 34);
        this.btnService.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        this.btnService.Location = new Point(694, 8);
        this.btnService.Cursor = Cursors.Hand;

        // 
        // Form1
        // 
        this.AutoScaleMode = AutoScaleMode.Font;
        this.ClientSize = new Size(800, 500);
        this.FormBorderStyle = FormBorderStyle.None;
        this.WindowState = FormWindowState.Maximized;
        this.BackColor = Color.FromArgb(20, 20, 25);
        this.StartPosition = FormStartPosition.CenterScreen;
        this.Text = "GVP-Pro";
        this.TopMost = true;

        // Add panels in correct order (fill must be added last)
        this.Controls.Add(this.pnlCenter);
        this.Controls.Add(this.pnlHeader);
        this.Controls.Add(this.pnlFooter);
    }

    #endregion

    private Label lblTitle;
    private Label lblVersion;
    private Label lblStatus;
    private Label lblCenterMessage;
    private Button btnService;
    private Button btnLogOff;
    private Panel pnlHeader;
    private Panel pnlFooter;
    private Panel pnlCenter;
}
