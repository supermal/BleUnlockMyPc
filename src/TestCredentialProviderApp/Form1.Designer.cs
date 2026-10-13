namespace TestCredentialProviderApp
{
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
            tableLayoutPanel1 = new TableLayoutPanel();
            btnInstall = new Button();
            label1 = new Label();
            label2 = new Label();
            txtUserName = new TextBox();
            txtPassword = new TextBox();
            btnLock = new Button();
            label3 = new Label();
            txtLog = new TextBox();
            tableLayoutPanel1.SuspendLayout();
            SuspendLayout();
            // 
            // tableLayoutPanel1
            // 
            tableLayoutPanel1.ColumnCount = 6;
            tableLayoutPanel1.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 10F));
            tableLayoutPanel1.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
            tableLayoutPanel1.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
            tableLayoutPanel1.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
            tableLayoutPanel1.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
            tableLayoutPanel1.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 10F));
            tableLayoutPanel1.Controls.Add(btnInstall, 1, 6);
            tableLayoutPanel1.Controls.Add(label1, 1, 2);
            tableLayoutPanel1.Controls.Add(label2, 1, 4);
            tableLayoutPanel1.Controls.Add(txtUserName, 1, 3);
            tableLayoutPanel1.Controls.Add(txtPassword, 1, 5);
            tableLayoutPanel1.Controls.Add(btnLock, 3, 6);
            tableLayoutPanel1.Controls.Add(label3, 1, 1);
            tableLayoutPanel1.Controls.Add(txtLog, 1, 7);
            tableLayoutPanel1.Dock = DockStyle.Fill;
            tableLayoutPanel1.Location = new Point(0, 0);
            tableLayoutPanel1.Name = "tableLayoutPanel1";
            tableLayoutPanel1.RowCount = 9;
            tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Absolute, 10F));
            tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
            tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
            tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
            tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
            tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
            tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Absolute, 35F));
            tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Absolute, 10F));
            tableLayoutPanel1.Size = new Size(477, 419);
            tableLayoutPanel1.TabIndex = 0;
            // 
            // btnInstall
            // 
            tableLayoutPanel1.SetColumnSpan(btnInstall, 2);
            btnInstall.Dock = DockStyle.Fill;
            btnInstall.Location = new Point(13, 163);
            btnInstall.Name = "btnInstall";
            btnInstall.Size = new Size(222, 29);
            btnInstall.TabIndex = 6;
            btnInstall.Text = "Install";
            btnInstall.UseVisualStyleBackColor = true;
            btnInstall.Visible = false;
            btnInstall.Click += btnInstall_Click;
            // 
            // label1
            // 
            label1.AutoSize = true;
            label1.Dock = DockStyle.Left;
            label1.Location = new Point(13, 40);
            label1.Name = "label1";
            label1.Size = new Size(62, 30);
            label1.TabIndex = 0;
            label1.Text = "UserName";
            label1.TextAlign = ContentAlignment.MiddleLeft;
            // 
            // label2
            // 
            label2.AutoSize = true;
            label2.Dock = DockStyle.Left;
            label2.Location = new Point(13, 100);
            label2.Name = "label2";
            label2.Size = new Size(57, 30);
            label2.TabIndex = 1;
            label2.Text = "Password";
            label2.TextAlign = ContentAlignment.MiddleLeft;
            // 
            // txtUserName
            // 
            tableLayoutPanel1.SetColumnSpan(txtUserName, 4);
            txtUserName.Dock = DockStyle.Fill;
            txtUserName.Location = new Point(13, 73);
            txtUserName.Name = "txtUserName";
            txtUserName.Size = new Size(450, 23);
            txtUserName.TabIndex = 2;
            // 
            // txtPassword
            // 
            tableLayoutPanel1.SetColumnSpan(txtPassword, 4);
            txtPassword.Dock = DockStyle.Fill;
            txtPassword.Location = new Point(13, 133);
            txtPassword.Name = "txtPassword";
            txtPassword.Size = new Size(450, 23);
            txtPassword.TabIndex = 3;
            txtPassword.UseSystemPasswordChar = true;
            // 
            // btnLock
            // 
            tableLayoutPanel1.SetColumnSpan(btnLock, 2);
            btnLock.Dock = DockStyle.Fill;
            btnLock.Location = new Point(241, 163);
            btnLock.Name = "btnLock";
            btnLock.Size = new Size(222, 29);
            btnLock.TabIndex = 4;
            btnLock.Text = "Lock";
            btnLock.UseVisualStyleBackColor = true;
            btnLock.Click += btnLock_Click;
            // 
            // label3
            // 
            label3.AutoSize = true;
            tableLayoutPanel1.SetColumnSpan(label3, 4);
            label3.Dock = DockStyle.Fill;
            label3.Font = new Font("Segoe UI", 12F, FontStyle.Bold);
            label3.Location = new Point(13, 10);
            label3.Name = "label3";
            label3.Size = new Size(450, 30);
            label3.TabIndex = 5;
            label3.Text = "Test Credential Provider App";
            label3.TextAlign = ContentAlignment.MiddleCenter;
            // 
            // txtLog
            // 
            tableLayoutPanel1.SetColumnSpan(txtLog, 4);
            txtLog.Dock = DockStyle.Fill;
            txtLog.Location = new Point(13, 198);
            txtLog.Multiline = true;
            txtLog.Name = "txtLog";
            txtLog.ScrollBars = ScrollBars.Vertical;
            txtLog.Size = new Size(450, 208);
            txtLog.TabIndex = 7;
            // 
            // Form1
            // 
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(477, 419);
            Controls.Add(tableLayoutPanel1);
            MaximizeBox = false;
            MinimizeBox = false;
            Name = "Form1";
            Text = "Form1";
            tableLayoutPanel1.ResumeLayout(false);
            tableLayoutPanel1.PerformLayout();
            ResumeLayout(false);
        }

        #endregion

        private TableLayoutPanel tableLayoutPanel1;
        private Label label1;
        private Label label2;
        private TextBox txtUserName;
        private TextBox txtPassword;
        private Button btnLock;
        private Label label3;
        private Button btnInstall;
        private TextBox txtLog;
    }
}
