﻿using System;
using System.Collections.ObjectModel;
using EditorUtils;
using Microsoft.FSharp.Core;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Vim;
using Vim.Extensions;
using Vim.UI.Wpf;

namespace VsVim
{
    /// <summary>
    /// This class needs to intercept commands which the core VIM engine wants to process and call into the VIM engine 
    /// directly.  It needs to be very careful to not double use commands that will be processed by the KeyProcessor.  In 
    /// general it just needs to avoid processing text input
    /// </summary>
    internal sealed class VsCommandTarget : IOleCommandTarget
    {
        /// <summary>
        /// This is the key which is used to store VsCommandTarget instances in the ITextView
        /// property bag
        /// </summary>
        private static readonly object Key = new object();

        private readonly ReadOnlyCollection<IEditCommandNode> _editCommandNodeList;
        private readonly IVim _vim;
        private readonly IVimBuffer _vimBuffer;
        private readonly ITextBuffer _textBuffer;
        private readonly IDisplayWindowBroker _broker;
        private readonly IVsAdapter _vsAdapter;
        private readonly IKeyUtil _keyUtil;
        private readonly IVimBufferCoordinator _bufferCoordinator;
        private IOleCommandTarget _nextTarget;

        private VsCommandTarget(
            IVimBufferCoordinator bufferCoordinator,
            IVsAdapter vsAdapter,
            IDisplayWindowBroker broker,
            IResharperUtil resharperUtil,
            IKeyUtil keyUtil)
        {
            _vim = bufferCoordinator.VimBuffer.Vim;
            _vimBuffer = bufferCoordinator.VimBuffer;
            _textBuffer = bufferCoordinator.VimBuffer.TextBuffer;
            _bufferCoordinator = bufferCoordinator;
            _broker = broker;
            _vsAdapter = vsAdapter;
            _keyUtil = keyUtil;

            var node = new TransitionCommandNode(
                bufferCoordinator,
                vsAdapter,
                broker,
                resharperUtil,
                keyUtil);
            var all = new IEditCommandNode[] { node };
            _editCommandNodeList = all.ToReadOnlyCollection();
        }

        /// <summary>
        /// Try and custom process the given InsertCommand when it's appropriate to override
        /// with Visual Studio specific behavior
        /// </summary>
        public bool TryCustomProcess(InsertCommand command)
        {
            var oleCommandData = OleCommandData.Empty;
            try
            {
                if (!TryGetOleCommandData(command, out oleCommandData))
                {
                    // Not a command that we custom process
                    return false;
                }

                if (_vim.InBulkOperation && !command.IsInsertNewLine)
                {
                    // If we are in the middle of a bulk operation we don't want to forward any
                    // input to IOleCommandTarget because it will trigger actions like displaying
                    // Intellisense.  Definitely don't want intellisense popping up during say a 
                    // repeat of a 'cw' operation or macro.
                    //
                    // The one exception to this rule though is the Enter key.  Every single language
                    // formats Enter in a special way that we absolutely want to preserve in a change
                    // or macro operation.  Go ahead and let it go through here and we'll dismiss 
                    // any intellisense which pops up as a result
                    return false;
                }

                var versionNumber = _textBuffer.CurrentSnapshot.Version.VersionNumber;
                int hr = _nextTarget.Exec(oleCommandData);

                // Whether or not an Exec succeeded is a bit of a heuristic.  IOleCommandTarget implementations like
                // C++ will return E_ABORT if Intellisense failed but the character was actually inserted into 
                // the ITextBuffer.  VsVim really only cares about the character insert.  However we must also
                // consider cases where the character successfully resulted in no action as a success
                return ErrorHandler.Succeeded(hr) || versionNumber < _textBuffer.CurrentSnapshot.Version.VersionNumber;
            }
            finally
            {
                if (oleCommandData != null)
                {
                    oleCommandData.Dispose();
                }

                if (_vim.InBulkOperation && _broker.IsCompletionActive)
                {
                    _broker.DismissDisplayWindows();
                }
            }
        }

        /// <summary>
        /// Try and convert the given insert command to an OleCommand.  This should only be done
        /// for InsertCommand values which we want to custom process
        /// </summary>
        private bool TryGetOleCommandData(InsertCommand command, out OleCommandData commandData)
        {
            if (command.IsBack)
            {
                commandData = new OleCommandData(VSConstants.VSStd2KCmdID.BACKSPACE);
                return true;
            }

            if (command.IsDelete)
            {
                commandData = new OleCommandData(VSConstants.VSStd2KCmdID.DELETE);
                return true;
            }

            if (command.IsDirectInsert)
            {
                var directInsert = (InsertCommand.DirectInsert)command;
                commandData = OleCommandData.CreateTypeChar(directInsert.Item);
                return true;
            }

            if (command.IsInsertTab)
            {
                commandData = new OleCommandData(VSConstants.VSStd2KCmdID.TAB);
                return true;
            }

            if (command.IsInsertNewLine)
            {
                commandData = new OleCommandData(VSConstants.VSStd2KCmdID.RETURN);
                return true;
            }

            commandData = OleCommandData.Empty;
            return false;
        }

        /// <summary>
        /// Try and convert the Visual Studio command to it's equivalent KeyInput
        /// </summary>
        internal bool TryConvert(Guid commandGroup, uint commandId, IntPtr variantIn, out KeyInput keyInput)
        {
            keyInput = null;

            EditCommand editCommand;
            if (!TryConvert(commandGroup, commandId, variantIn, out editCommand))
            {
                return false;
            }

            if (!editCommand.HasKeyInput)
            {
                return false;
            }

            keyInput = editCommand.KeyInput;
            return true;
        }

        /// <summary>
        /// Try and convert the Visual Studio command to it's equivalent KeyInput
        /// </summary>
        internal bool TryConvert(Guid commandGroup, uint commandId, IntPtr variantIn, out EditCommand editCommand)
        {
            editCommand = null;

            // Don't ever process a command when we are in an automation function.  Doing so will cause VsVim to 
            // intercept items like running Macros and certain wizard functionality
            if (_vsAdapter.InAutomationFunction)
            {
                return false;
            }

            // Don't intercept commands while incremental search is active.  Don't want to interfere with it
            if (_vsAdapter.IsIncrementalSearchActive(_vimBuffer.TextView))
            {
                return false;
            }

            var modifiers = _keyUtil.GetKeyModifiers(_vsAdapter.KeyboardDevice.Modifiers);
            if (!OleCommandUtil.TryConvert(commandGroup, commandId, variantIn, modifiers, out editCommand))
            {
                return false;
            }

            // Don't process Visual Studio commands.  If the key sequence is mapped to a Visual Studio command
            // then that command wins.
            if (editCommand.EditCommandKind == EditCommandKind.VisualStudioCommand)
            {
                return false;
            }

            return true;
        }

        private bool Exec(EditCommand editCommand)
        {
            foreach (var editCommandNode in _editCommandNodeList)
            {
                if (editCommandNode.Execute(editCommand))
                {
                    return true;
                }
            }

            return false;
        }

        private EditCommandStatus QueryStatus(EditCommand editCommand)
        {
            foreach (var editCommandNode in _editCommandNodeList)
            {
                var editCommandStatus = editCommandNode.QueryStatus(editCommand);
                if (editCommandStatus != EditCommandStatus.PassOn)
                {
                    return editCommandStatus;
                }
            }

            return EditCommandStatus.PassOn;
        }

        int IOleCommandTarget.Exec(ref Guid commandGroup, uint commandId, uint commandExecOpt, IntPtr variantIn, IntPtr variantOut)
        {
            EditCommand editCommand = null;
            try
            {
                if (TryConvert(commandGroup, commandId, variantIn, out editCommand) &&
                    Exec(editCommand))
                {
                    return NativeMethods.S_OK;
                }

                return _nextTarget.Exec(commandGroup, commandId, commandExecOpt, variantIn, variantOut);
            }
            finally
            {
                _bufferCoordinator.DiscardedKeyInput = FSharpOption<KeyInput>.None;

                // The GoToDefinition command will often cause a selection to occur in the 
                // buffer.  We don't want that to cause us to enter Visual Mode so clear it
                // out 
                if (editCommand != null &&
                    editCommand.EditCommandKind == EditCommandKind.GoToDefinition &&
                    !_vimBuffer.TextView.Selection.IsEmpty)
                {
                    _vimBuffer.TextView.Selection.Clear();
                }
            }
        }

        int IOleCommandTarget.QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
        {
            EditCommand editCommand;
            if (1 == cCmds && TryConvert(pguidCmdGroup, prgCmds[0].cmdID, pCmdText, out editCommand))
            {
                VimTrace.TraceInfo("VsCommandTarget::QueryStatus {0}", editCommand);

                _bufferCoordinator.DiscardedKeyInput = FSharpOption<KeyInput>.None;

                switch (QueryStatus(editCommand))
                {
                    case EditCommandStatus.Enable:
                        prgCmds[0].cmdf = (uint)(OLECMDF.OLECMDF_ENABLED | OLECMDF.OLECMDF_SUPPORTED);
                        return NativeMethods.S_OK;
                    case EditCommandStatus.Disable:
                        prgCmds[0].cmdf = (uint)OLECMDF.OLECMDF_SUPPORTED;
                        return NativeMethods.S_OK;
                    case EditCommandStatus.PassOn:
                        return _nextTarget.QueryStatus(pguidCmdGroup, cCmds, prgCmds, pCmdText);
                }

                //VimTrace.TraceInfo("VsCommandTarget::QueryStatus ", action);
            }

            return _nextTarget.QueryStatus(pguidCmdGroup, cCmds, prgCmds, pCmdText);
        }

        internal static Result<VsCommandTarget> Create(
            IVimBufferCoordinator bufferCoordinator,
            IVsTextView vsTextView,
            IVsAdapter adapter,
            IDisplayWindowBroker broker,
            IResharperUtil resharperUtil,
            IKeyUtil keyUtil)
        {
            var vsCommandTarget = new VsCommandTarget(bufferCoordinator, adapter, broker, resharperUtil, keyUtil);
            var hresult = vsTextView.AddCommandFilter(vsCommandTarget, out vsCommandTarget._nextTarget);
            var result = Result.CreateSuccessOrError(vsCommandTarget, hresult);
            if (result.IsSuccess)
            {
                bufferCoordinator.VimBuffer.TextView.Properties[Key] = vsCommandTarget;
            }

            return result;
        }

        internal static bool TryGet(ITextView textView, out VsCommandTarget vsCommandTarget)
        {
            return textView.Properties.TryGetPropertySafe(Key, out vsCommandTarget);
        }
    }
}
