﻿using System;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace VssSvnConverter
{
	public partial class SimpleUI : Form
	{
		public SimpleUI()
		{
			InitializeComponent();
		}

		private void buildList_Click(object sender, EventArgs e)
		{
			var btn = (Button)sender;
			btn.BackColor = Color.Yellow;
			Controls.Cast<Control>().ToList().ForEach(c => c.Enabled = false);

			new Thread(() => {
				Program.ProcessStage(btn.Tag as string);
				Action action = () => {
					btn.BackColor = Color.PaleGreen;
					Controls.Cast<Control>().ToList().ForEach(c => c.Enabled = true);
				};
				Invoke(action);
			}).Start();
		}
	}
}
