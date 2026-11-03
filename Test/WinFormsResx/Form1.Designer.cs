// Copyright (C) 2026- marinusmaurice@outlook.com
namespace WinFormsResx {
	partial class Form1 {
		private System.ComponentModel.IContainer components = null;
		private System.Windows.Forms.ImageList imageList1;
		private System.Windows.Forms.Label label1;

		protected override void Dispose(bool disposing) {
			if (disposing && (components != null))
				components.Dispose();
			base.Dispose(disposing);
		}

		private void InitializeComponent() {
			components = new System.ComponentModel.Container();
			System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(Form1));
			imageList1 = new System.Windows.Forms.ImageList(components);
			label1 = new System.Windows.Forms.Label();
			SuspendLayout();
			imageList1.ImageStream = (System.Windows.Forms.ImageListStreamer)resources.GetObject("imageList1.ImageStream");
			imageList1.TransparentColor = System.Drawing.Color.Transparent;
			label1.Text = resources.GetString("label1.Text");
			label1.BackColor = (System.Drawing.Color)resources.GetObject("label1.BackColor");
			label1.Image = (System.Drawing.Image)resources.GetObject("label1.Image");
			Controls.Add(label1);
			Icon = (System.Drawing.Icon)resources.GetObject("$this.Icon");
			Text = "Form1";
			ResumeLayout(false);
		}
	}
}
