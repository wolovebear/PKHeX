﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Media;
using System.Threading.Tasks;
using System.Windows.Forms;
using PKHeX.Core;
using PKHeX.WinForms.Properties;

namespace PKHeX.WinForms.Controls
{
    /// <summary>
    /// Orchestrates the movement of slots within the GUI.
    /// </summary>
    public sealed class SlotChangeManager : IDisposable
    {
        public readonly SAVEditor SE;
        public readonly SlotTrackerImage LastSlot = new SlotTrackerImage();
        public readonly DragManager Drag = new DragManager();
        public SaveDataEditor<PictureBox> Env { get; set; }

        private SaveFile SAV => SE.SAV;
        public readonly List<BoxEditor> Boxes = new List<BoxEditor>();
        public readonly SlotHoverHandler Hover = new SlotHoverHandler();

        public SlotChangeManager(SAVEditor se) => SE = se;

        public void Reset()
        {
            Drag.Initialize();
            LastSlot.Reset();
        }

        public void MouseEnter(object sender, EventArgs e)
        {
            var pb = (PictureBox)sender;
            if (pb.Image == null)
                return;
            Hover.Start(pb, LastSlot);
        }

        public void MouseLeave(object sender, EventArgs e)
        {
            Hover.Stop();
        }

        public void MouseClick(object sender, MouseEventArgs e)
        {
            if (!Drag.Info.DragDropInProgress)
                SE.ClickSlot(sender, e);
        }

        public void MouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
                Drag.Info.LeftMouseIsDown = false;
        }

        public void MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                Drag.Info.LeftMouseIsDown = true;
                Drag.MouseDownPosition = Cursor.Position;
            }
        }

        public void QueryContinueDrag(object sender, QueryContinueDragEventArgs e)
        {
            if (e.Action != DragAction.Cancel && e.Action != DragAction.Drop)
                return;
            Drag.Info.LeftMouseIsDown = false;
            Drag.Info.DragDropInProgress = false;
        }

        public void DragEnter(object sender, DragEventArgs e)
        {
            if (e.AllowedEffect == (DragDropEffects.Copy | DragDropEffects.Link)) // external file
                e.Effect = DragDropEffects.Copy;
            else if (e.Data != null) // within
                e.Effect = DragDropEffects.Move;

            if (Drag.Info.DragDropInProgress)
                Drag.SetCursor(((Control)sender).FindForm(), Drag.Info.Cursor);
        }

        public void MouseMove(object sender, MouseEventArgs e)
        {
            if (!Drag.CanStartDrag)
                return;

            // Abort if there is no Pokemon in the given slot.
            PictureBox pb = (PictureBox)sender;
            if (pb.Image == null)
                return;
            var view = WinFormsUtil.FindFirstControlOfType<ISlotViewer<PictureBox>>(pb);
            var src = view.GetSlotData(pb);
            if (!src.CanWriteTo(SAV))
                return;
            bool encrypt = Control.ModifierKeys == Keys.Control;
            HandleMovePKM(pb, encrypt);
        }

        public void DragDrop(object sender, DragEventArgs e)
        {
            PictureBox pb = (PictureBox)sender;
            var view = WinFormsUtil.FindFirstControlOfType<ISlotViewer<PictureBox>>(pb);
            var src = view.GetSlotData(pb);
            if (!src.CanWriteTo(SAV))
            {
                SystemSounds.Asterisk.Play();
                e.Effect = DragDropEffects.Copy;
                Drag.Reset();
                return;
            }

            var mod = SlotUtil.GetDropModifier();
            Drag.Info.Destination = src;
            HandleDropPKM(pb, e, mod);
        }

        private static ISlotViewer<T> GetViewParent<T>(T pb) where T : Control
            => WinFormsUtil.FindFirstControlOfType<ISlotViewer<T>>(pb);

        private void HandleMovePKM(PictureBox pb, bool encrypt)
        {
            // Create a temporary PKM file to perform a drag drop operation.

            // Set flag to prevent re-entering.
            Drag.Info.DragDropInProgress = true;

            // Prepare Data
            Drag.Info.Source = GetViewParent(pb).GetSlotData(pb);
            Drag.Info.Source.Read(SAV);

            // Make a new file name based off the PID
            string newfile = CreateDragDropPKM(pb, encrypt, out bool external);

            // drop finished, clean up
            Drag.Reset();
            Drag.ResetCursor(pb.FindForm());

            // Browser apps need time to load data since the file isn't moved to a location on the user's local storage.
            // Tested 10ms -> too quick, 100ms was fine. 500ms should be safe?
            // Keep it to 10 seconds; Discord upload only stores the file path until you click Upload.
            int delay = external ? 10_000 : 0;
            DeleteAsync(newfile, delay);
            if (Drag.Info.Source is SlotInfoParty || Drag.Info.Destination is SlotInfoParty)
                SE.SetParty();
        }

        private async void DeleteAsync(string path, int delay)
        {
            await Task.Delay(delay).ConfigureAwait(true);
            if (File.Exists(path) && Drag.Info.CurrentPath == null)
                File.Delete(path);
        }

        private string CreateDragDropPKM(PictureBox pb, bool encrypt, out bool external)
        {
            // Make File
            PKM pk = Drag.Info.Source.Read(SAV);
            string newfile = FileUtil.GetPKMTempFileName(pk, encrypt);
            try
            {
                var data = encrypt ? pk.EncryptedBoxData : pk.DecryptedBoxData;
                external = TryMakeDragDropPKM(pb, data, newfile);
            }
            catch (Exception x)
            {
                WinFormsUtil.Error("Drag & Drop Error", x);
                external = false;
            }

            return newfile;
        }

        private bool TryMakeDragDropPKM(PictureBox pb, byte[] data, string newfile)
        {
            File.WriteAllBytes(newfile, data);
            var img = (Bitmap)pb.Image;
            Drag.SetCursor(pb.FindForm(), new Cursor(img.GetHicon()));
            Hover.Stop();
            pb.Image = null;
            pb.BackgroundImage = Resources.slotDrag;

            // Thread Blocks on DoDragDrop
            Drag.Info.CurrentPath = newfile;
            var result = pb.DoDragDrop(new DataObject(DataFormats.FileDrop, new[] { newfile }), DragDropEffects.Move);
            var external = Drag.Info.Source == null || result != DragDropEffects.Link;
            if (external || Drag.Info.Source.Equals(Drag.Info.Destination)) // not dropped to another box slot, restore img
            {
                pb.Image = img;
                pb.BackgroundImage = LastSlot.OriginalBackground;
                Drag.ResetCursor(pb.FindForm());
                return external;
            }

            if (result == DragDropEffects.Copy) // viewed in tabs or cloned
            {
                if (Drag.Info.Destination == null) // apply 'view' highlight
                    Env.Slots.Get(Drag.Info.Source);
                return false;
            }
            return true;
        }

        private void HandleDropPKM(PictureBox pb, DragEventArgs e, DropModifier mod)
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (Directory.Exists(files[0])) // folder
            {
                SE.LoadBoxes(out string _, files[0]);
                return;
            }

            e.Effect = mod == DropModifier.Clone ? DragDropEffects.Copy : DragDropEffects.Link;

            // file
            Drag.Info.Destination = GetViewParent(pb).GetSlotData(pb);
            if (Drag.Info.SameLocation)
            {
                e.Effect = DragDropEffects.Link;
                return;
            }

            var dest = Drag.Info.Destination;

            if (Drag.Info.Source == null) // external source
            {
                bool badDest = !dest.CanWriteTo(SAV);
                if (!TryLoadFiles(files, e, badDest))
                    WinFormsUtil.Alert(MessageStrings.MsgSaveSlotBadData);
            }
            else if (!TrySetPKMDestination(pb, mod))
            {
                WinFormsUtil.Alert(MessageStrings.MsgSaveSlotEmpty);
            }
            Drag.Reset();
        }

        /// <summary>
        /// Tries to load the input <see cref="files"/>
        /// </summary>
        /// <param name="files">Files to load</param>
        /// <param name="e">Args</param>
        /// <param name="badDest">Destination slot disallows eggs/blanks</param>
        /// <returns>True if loaded</returns>
        private bool TryLoadFiles(IReadOnlyList<string> files, DragEventArgs e, bool badDest)
        {
            if (files.Count == 0)
                return false;

            var sav = SAV;
            var path = files[0];
            var temp = FileUtil.GetSingleFromPath(path, sav);
            if (temp == null)
            {
                Drag.RequestDD(this, e); // pass thru
                return true; // treat as handled
            }

            PKM pk = PKMConverter.ConvertToType(temp, sav.PKMType, out string c);
            if (pk == null)
            {
                WinFormsUtil.Error(c);
                Debug.WriteLine(c);
                return false;
            }

            if (badDest && (pk.Species == 0 || pk.IsEgg))
                return false;

            if (sav is ILangDeviantSave il && PKMConverter.IsIncompatibleGB(pk.Format, il.Japanese, pk.Japanese))
            {
                c = PKMConverter.GetIncompatibleGBMessage(pk, il.Japanese);
                WinFormsUtil.Error(c);
                Debug.WriteLine(c);
                return false;
            }

            var errata = sav.IsPKMCompatible(pk);
            if (errata.Count > 0)
            {
                string concat = string.Join(Environment.NewLine, errata);
                if (DialogResult.Yes != WinFormsUtil.Prompt(MessageBoxButtons.YesNo, concat, MessageStrings.MsgContinue))
                {
                    Debug.WriteLine(c);
                    Debug.WriteLine(concat);
                    return false;
                }
            }

            Env.Slots.Set(Drag.Info.Destination, pk);
            Debug.WriteLine(c);
            return true;
        }

        private bool TrySetPKMDestination(PictureBox pb, DropModifier mod)
        {
            PKM pk = Drag.Info.Source.Read(SAV);
            var msg = Drag.Info.Destination.CanWriteTo(SAV, pk);
            if (msg != WriteBlockedMessage.None)
                return false;

            if (Drag.Info.Source != null)
                TrySetPKMSource(pb, mod);

            // Copy from temp to destination slot.
            Env.Slots.Set(Drag.Info.Destination, pk);
            Drag.ResetCursor(pb.FindForm());
            return true;
        }

        private bool TrySetPKMSource(PictureBox sender, DropModifier mod)
        {
            if (Drag.Info.Destination == null || mod == DropModifier.Clone)
                return false;

            if (sender.Image == null || mod == DropModifier.Overwrite)
            {
                Env.Slots.Delete(Drag.Info.Source);
                return true;
            }

            var pk = Drag.Info.Destination.Read(SAV);
            Env.Slots.Set(Drag.Info.Source, pk);
            return true;
        }

        // Utility
        public void SwapBoxes(int index, int other)
        {
            if (index == other)
                return;
            SAV.SwapBox(index, other);
            UpdateBoxViewAtBoxIndexes(index, other);
        }

        public void Dispose()
        {
            Hover.Dispose();
            SE?.Dispose();
            LastSlot.OriginalBackground?.Dispose();
            LastSlot.CurrentBackground?.Dispose();
        }

        private void UpdateBoxViewAtBoxIndexes(params int[] boxIndexes)
        {
            foreach (var box in Boxes)
            {
                var current = box.CurrentBox;
                if (!boxIndexes.Contains(current))
                    continue;
                box.ResetSlots();
                box.ResetBoxNames(current);
            }
        }
    }
}
