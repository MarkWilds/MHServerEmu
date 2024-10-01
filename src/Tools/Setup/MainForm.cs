﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Setup
{
    public partial class MainForm : Form
    {
        private enum SetupState
        {
            Invalid,
            Start,
            SelectFolder,
            Complete
        }

        private SetupState _state = SetupState.Invalid;

        public MainForm()
        {
            InitializeComponent();
            AdvanceState();
        }

        private void nextButton_Click(object sender, EventArgs e)
        {
            switch (_state)
            {
                case SetupState.Start:
                    AdvanceState();
                    return;

                case SetupState.SelectFolder:
                    (bool result, string message) = SetupHelper.RunSetup(folderBrowseTextBox.Text);
                    if (result == false)
                    {
                        MessageBox.Show(message, "Setup Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }

                    AdvanceState();
                    return;

                case SetupState.Complete:
                    Application.Exit();
                    return;
            }
        }

        private void folderBrowseButton_Click(object sender, EventArgs e)
        {
            using (FolderBrowserDialog dialog = new())
            {
                DialogResult dialogResult = dialog.ShowDialog(this);
                if (dialogResult != DialogResult.OK) return;
                folderBrowseTextBox.Text = dialog.SelectedPath;
            }
        }

        private void AdvanceState()
        {
            _state++;
            UpdateForm();
        }

        private void UpdateForm()
        {
            switch (_state)
            {
                case SetupState.Start:
                    headerLabel.Text = "Welcome to the MHServerEmu Setup Sorcerer Supreme";
                    bodyLabel.Text = "This program will help you set up MHServerEmu.\r\n\r\nTo continue, click Next.";
                    break;

                case SetupState.SelectFolder:
                    headerLabel.Text = "Marvel Heroes Files";
                    bodyLabel.Text = "Please choose the folder in which Marvel Heroes game files are located.";

                    folderBrowseTextBox.Visible = true;
                    folderBrowseButton.Visible = true;

                    break;

                case SetupState.Complete:
                    headerLabel.Text = "Setup Complete";
                    bodyLabel.Text = "Setup successful.\r\n\r\nRun StartClient.bat to launch the game normally.\r\n\r\nRun StartClientAutoLogin.bat to launch the game and automatically log in with a default account.";
                    nextButton.Text = "Exit";

                    folderBrowseTextBox.Visible = false;
                    folderBrowseButton.Visible = false;

                    break;

                default:
                    headerLabel.Text = "Invalid Setup State";
                    bodyLabel.Text = string.Empty;
                    break;
            }
        }
    }
}