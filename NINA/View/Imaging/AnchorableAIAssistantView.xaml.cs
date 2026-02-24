#region "copyright"

/*
    Copyright (c) 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Windows.Controls;
using System.Windows.Input;
using NINA.WPF.Base.Interfaces.ViewModel;

namespace NINA.View {

    public partial class AnchorableAIAssistantView : UserControl {

        public AnchorableAIAssistantView() {
            InitializeComponent();
        }

        private void PromptTextBox_OnKeyDown(object sender, KeyEventArgs e) {
            if (e.Key != Key.Enter) {
                return;
            }

            if (DataContext is IAIAssistantVM vm && vm.SendPromptCommand?.CanExecute(null) == true) {
                vm.SendPromptCommand.Execute(null);
                e.Handled = true;
            }
        }
    }
}
