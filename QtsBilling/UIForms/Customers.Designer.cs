namespace QtsBilling.UIForms
{
    partial class Customers
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
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
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            tableLayoutPanel1 = new TableLayoutPanel();
            tableLayoutPanel2 = new TableLayoutPanel();
            BtnSaveCustomer = new Button();
            BtnDeleteCustomer = new Button();
            BtnUpdateCustomer = new Button();
            BtnAddCustomer = new Button();
            tableLayoutPanel3 = new TableLayoutPanel();
            TextBoxForAddress = new TextBox();
            TextBoxForMobile = new TextBox();
            TextBoxForEmail = new TextBox();
            label7 = new Label();
            label5 = new Label();
            label3 = new Label();
            label1 = new Label();
            TextBoxForName = new TextBox();
            DgvCustomers = new DataGridView();
            tableLayoutPanel4 = new TableLayoutPanel();
            label2 = new Label();
            TextBoxForSearchCustomers = new TextBox();
            tableLayoutPanel1.SuspendLayout();
            tableLayoutPanel2.SuspendLayout();
            tableLayoutPanel3.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)DgvCustomers).BeginInit();
            tableLayoutPanel4.SuspendLayout();
            SuspendLayout();
            // 
            // tableLayoutPanel1
            // 
            tableLayoutPanel1.ColumnCount = 3;
            tableLayoutPanel1.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 5F));
            tableLayoutPanel1.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 90F));
            tableLayoutPanel1.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 5F));
            tableLayoutPanel1.Controls.Add(tableLayoutPanel2, 1, 0);
            tableLayoutPanel1.Controls.Add(tableLayoutPanel3, 1, 1);
            tableLayoutPanel1.Controls.Add(DgvCustomers, 1, 3);
            tableLayoutPanel1.Controls.Add(tableLayoutPanel4, 1, 2);
            tableLayoutPanel1.Dock = DockStyle.Fill;
            tableLayoutPanel1.Location = new Point(0, 0);
            tableLayoutPanel1.Name = "tableLayoutPanel1";
            tableLayoutPanel1.RowCount = 4;
            tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Percent, 15.6140347F));
            tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Percent, 21.9298248F));
            tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Percent, 8.596491F));
            tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Percent, 54.2105255F));
            tableLayoutPanel1.Size = new Size(1025, 570);
            tableLayoutPanel1.TabIndex = 0;
            // 
            // tableLayoutPanel2
            // 
            tableLayoutPanel2.ColumnCount = 4;
            tableLayoutPanel2.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
            tableLayoutPanel2.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
            tableLayoutPanel2.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
            tableLayoutPanel2.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
            tableLayoutPanel2.Controls.Add(BtnSaveCustomer, 3, 0);
            tableLayoutPanel2.Controls.Add(BtnDeleteCustomer, 2, 0);
            tableLayoutPanel2.Controls.Add(BtnUpdateCustomer, 1, 0);
            tableLayoutPanel2.Controls.Add(BtnAddCustomer, 0, 0);
            tableLayoutPanel2.Location = new Point(54, 3);
            tableLayoutPanel2.Name = "tableLayoutPanel2";
            tableLayoutPanel2.RowCount = 1;
            tableLayoutPanel2.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            tableLayoutPanel2.Size = new Size(916, 82);
            tableLayoutPanel2.TabIndex = 0;
            // 
            // BtnSaveCustomer
            // 
            BtnSaveCustomer.Dock = DockStyle.Fill;
            BtnSaveCustomer.Font = new Font("Arial", 11.25F, FontStyle.Bold, GraphicsUnit.Point, 0);
            BtnSaveCustomer.Location = new Point(690, 3);
            BtnSaveCustomer.Name = "BtnSaveCustomer";
            BtnSaveCustomer.Size = new Size(223, 76);
            BtnSaveCustomer.TabIndex = 3;
            BtnSaveCustomer.Text = "Save";
            BtnSaveCustomer.UseVisualStyleBackColor = true;
            BtnSaveCustomer.Click += BtnSaveCustomer_Click;
            // 
            // BtnDeleteCustomer
            // 
            BtnDeleteCustomer.Dock = DockStyle.Fill;
            BtnDeleteCustomer.Font = new Font("Arial", 11.25F, FontStyle.Bold, GraphicsUnit.Point, 0);
            BtnDeleteCustomer.Location = new Point(461, 3);
            BtnDeleteCustomer.Name = "BtnDeleteCustomer";
            BtnDeleteCustomer.Size = new Size(223, 76);
            BtnDeleteCustomer.TabIndex = 2;
            BtnDeleteCustomer.Text = "Delete Customer";
            BtnDeleteCustomer.UseVisualStyleBackColor = true;
            BtnDeleteCustomer.Click += BtnDeleteCustomer_Click;
            // 
            // BtnUpdateCustomer
            // 
            BtnUpdateCustomer.Dock = DockStyle.Fill;
            BtnUpdateCustomer.Font = new Font("Arial", 11.25F, FontStyle.Bold, GraphicsUnit.Point, 0);
            BtnUpdateCustomer.Location = new Point(232, 3);
            BtnUpdateCustomer.Name = "BtnUpdateCustomer";
            BtnUpdateCustomer.Size = new Size(223, 76);
            BtnUpdateCustomer.TabIndex = 1;
            BtnUpdateCustomer.Text = "Update Customer";
            BtnUpdateCustomer.UseVisualStyleBackColor = true;
            BtnUpdateCustomer.Click += BtnUpdateCustomer_Click;
            // 
            // BtnAddCustomer
            // 
            BtnAddCustomer.Dock = DockStyle.Fill;
            BtnAddCustomer.Font = new Font("Arial", 11.25F, FontStyle.Bold, GraphicsUnit.Point, 0);
            BtnAddCustomer.Location = new Point(3, 3);
            BtnAddCustomer.Name = "BtnAddCustomer";
            BtnAddCustomer.Size = new Size(223, 76);
            BtnAddCustomer.TabIndex = 0;
            BtnAddCustomer.Text = "Add Customer";
            BtnAddCustomer.UseVisualStyleBackColor = true;
            BtnAddCustomer.Click += BtnAddCustomer_Click;
            // 
            // tableLayoutPanel3
            // 
            tableLayoutPanel3.ColumnCount = 2;
            tableLayoutPanel3.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 10.48035F));
            tableLayoutPanel3.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 89.51965F));
            tableLayoutPanel3.Controls.Add(TextBoxForAddress, 1, 3);
            tableLayoutPanel3.Controls.Add(TextBoxForMobile, 1, 2);
            tableLayoutPanel3.Controls.Add(TextBoxForEmail, 1, 1);
            tableLayoutPanel3.Controls.Add(label7, 0, 3);
            tableLayoutPanel3.Controls.Add(label5, 0, 2);
            tableLayoutPanel3.Controls.Add(label3, 0, 1);
            tableLayoutPanel3.Controls.Add(label1, 0, 0);
            tableLayoutPanel3.Controls.Add(TextBoxForName, 1, 0);
            tableLayoutPanel3.Dock = DockStyle.Fill;
            tableLayoutPanel3.Location = new Point(54, 91);
            tableLayoutPanel3.Name = "tableLayoutPanel3";
            tableLayoutPanel3.RowCount = 4;
            tableLayoutPanel3.RowStyles.Add(new RowStyle(SizeType.Percent, 25F));
            tableLayoutPanel3.RowStyles.Add(new RowStyle(SizeType.Percent, 25F));
            tableLayoutPanel3.RowStyles.Add(new RowStyle(SizeType.Percent, 25F));
            tableLayoutPanel3.RowStyles.Add(new RowStyle(SizeType.Percent, 25F));
            tableLayoutPanel3.Size = new Size(916, 118);
            tableLayoutPanel3.TabIndex = 2;
            // 
            // TextBoxForAddress
            // 
            TextBoxForAddress.BorderStyle = BorderStyle.FixedSingle;
            TextBoxForAddress.Dock = DockStyle.Fill;
            TextBoxForAddress.Font = new Font("Arial", 11.25F, FontStyle.Regular, GraphicsUnit.Point, 0);
            TextBoxForAddress.Location = new Point(99, 90);
            TextBoxForAddress.Name = "TextBoxForAddress";
            TextBoxForAddress.Size = new Size(814, 25);
            TextBoxForAddress.TabIndex = 10;
            // 
            // TextBoxForMobile
            // 
            TextBoxForMobile.BorderStyle = BorderStyle.FixedSingle;
            TextBoxForMobile.Dock = DockStyle.Fill;
            TextBoxForMobile.Font = new Font("Arial", 11.25F, FontStyle.Regular, GraphicsUnit.Point, 0);
            TextBoxForMobile.Location = new Point(99, 61);
            TextBoxForMobile.Name = "TextBoxForMobile";
            TextBoxForMobile.Size = new Size(814, 25);
            TextBoxForMobile.TabIndex = 9;
            // 
            // TextBoxForEmail
            // 
            TextBoxForEmail.BorderStyle = BorderStyle.FixedSingle;
            TextBoxForEmail.Dock = DockStyle.Fill;
            TextBoxForEmail.Font = new Font("Arial", 11.25F, FontStyle.Regular, GraphicsUnit.Point, 0);
            TextBoxForEmail.Location = new Point(99, 32);
            TextBoxForEmail.Name = "TextBoxForEmail";
            TextBoxForEmail.Size = new Size(814, 25);
            TextBoxForEmail.TabIndex = 8;
            // 
            // label7
            // 
            label7.AutoSize = true;
            label7.Dock = DockStyle.Left;
            label7.Font = new Font("Arial", 12F, FontStyle.Bold, GraphicsUnit.Point, 0);
            label7.Location = new Point(3, 87);
            label7.Name = "label7";
            label7.Size = new Size(83, 31);
            label7.TabIndex = 6;
            label7.Text = "Address :";
            // 
            // label5
            // 
            label5.AutoSize = true;
            label5.Dock = DockStyle.Left;
            label5.Font = new Font("Arial", 12F, FontStyle.Bold, GraphicsUnit.Point, 0);
            label5.Location = new Point(3, 58);
            label5.Name = "label5";
            label5.Size = new Size(69, 29);
            label5.TabIndex = 4;
            label5.Text = "Mobile :";
            // 
            // label3
            // 
            label3.AutoSize = true;
            label3.Dock = DockStyle.Left;
            label3.Font = new Font("Arial", 12F, FontStyle.Bold, GraphicsUnit.Point, 0);
            label3.Location = new Point(3, 29);
            label3.Name = "label3";
            label3.Size = new Size(61, 29);
            label3.TabIndex = 2;
            label3.Text = "Email :";
            // 
            // label1
            // 
            label1.AutoSize = true;
            label1.Dock = DockStyle.Left;
            label1.Font = new Font("Arial", 12F, FontStyle.Bold, GraphicsUnit.Point, 0);
            label1.Location = new Point(3, 0);
            label1.Name = "label1";
            label1.Size = new Size(63, 29);
            label1.TabIndex = 0;
            label1.Text = "Name :";
            // 
            // TextBoxForName
            // 
            TextBoxForName.BorderStyle = BorderStyle.FixedSingle;
            TextBoxForName.Dock = DockStyle.Fill;
            TextBoxForName.Font = new Font("Arial", 11.25F, FontStyle.Regular, GraphicsUnit.Point, 0);
            TextBoxForName.Location = new Point(99, 3);
            TextBoxForName.Name = "TextBoxForName";
            TextBoxForName.Size = new Size(814, 25);
            TextBoxForName.TabIndex = 7;
            // 
            // DgvCustomers
            // 
            DgvCustomers.AllowUserToAddRows = false;
            DgvCustomers.AllowUserToDeleteRows = false;
            DgvCustomers.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            DgvCustomers.BackgroundColor = SystemColors.ButtonFace;
            DgvCustomers.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            DgvCustomers.Dock = DockStyle.Fill;
            DgvCustomers.Location = new Point(54, 263);
            DgvCustomers.Name = "DgvCustomers";
            DgvCustomers.ReadOnly = true;
            DgvCustomers.Size = new Size(916, 304);
            DgvCustomers.TabIndex = 1;
            DgvCustomers.CellClick += DgvCustomers_CellClick;
            // 
            // tableLayoutPanel4
            // 
            tableLayoutPanel4.ColumnCount = 2;
            tableLayoutPanel4.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 18.0131F));
            tableLayoutPanel4.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 81.9869F));
            tableLayoutPanel4.Controls.Add(label2, 0, 0);
            tableLayoutPanel4.Controls.Add(TextBoxForSearchCustomers, 1, 0);
            tableLayoutPanel4.Dock = DockStyle.Fill;
            tableLayoutPanel4.Location = new Point(54, 215);
            tableLayoutPanel4.Name = "tableLayoutPanel4";
            tableLayoutPanel4.RowCount = 1;
            tableLayoutPanel4.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            tableLayoutPanel4.Size = new Size(916, 42);
            tableLayoutPanel4.TabIndex = 3;
            // 
            // label2
            // 
            label2.AutoSize = true;
            label2.Dock = DockStyle.Right;
            label2.Font = new Font("Arial", 11.25F, FontStyle.Bold, GraphicsUnit.Point, 0);
            label2.Location = new Point(23, 0);
            label2.Name = "label2";
            label2.Size = new Size(139, 42);
            label2.TabIndex = 0;
            label2.Text = "Search Customers";
            // 
            // TextBoxForSearchCustomers
            // 
            TextBoxForSearchCustomers.Dock = DockStyle.Fill;
            TextBoxForSearchCustomers.Font = new Font("Arial", 9.75F, FontStyle.Regular, GraphicsUnit.Point, 0);
            TextBoxForSearchCustomers.Location = new Point(168, 3);
            TextBoxForSearchCustomers.Name = "TextBoxForSearchCustomers";
            TextBoxForSearchCustomers.Size = new Size(745, 22);
            TextBoxForSearchCustomers.TabIndex = 1;
            TextBoxForSearchCustomers.TextChanged += TextBoxForSearchCustomers_TextChanged;
            // 
            // Customers
            // 
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(1025, 570);
            Controls.Add(tableLayoutPanel1);
            Name = "Customers";
            Text = "Customers";
            Load += Customers_Load;
            tableLayoutPanel1.ResumeLayout(false);
            tableLayoutPanel2.ResumeLayout(false);
            tableLayoutPanel3.ResumeLayout(false);
            tableLayoutPanel3.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)DgvCustomers).EndInit();
            tableLayoutPanel4.ResumeLayout(false);
            tableLayoutPanel4.PerformLayout();
            ResumeLayout(false);
        }

        #endregion

        private TableLayoutPanel tableLayoutPanel1;
        private TableLayoutPanel tableLayoutPanel2;
        private Button BtnAddCustomer;
        private Button BtnSaveCustomer;
        private Button BtnDeleteCustomer;
        private Button BtnUpdateCustomer;
        private DataGridView DgvCustomers;
        private TableLayoutPanel tableLayoutPanel3;
        private Label label7;
        private Label label5;
        private Label label3;
        private Label label1;
        private TextBox TextBoxForAddress;
        private TextBox TextBoxForMobile;
        private TextBox TextBoxForEmail;
        private TextBox TextBoxForName;
        private TableLayoutPanel tableLayoutPanel4;
        private Label label2;
        private TextBox TextBoxForSearchCustomers;
    }
}