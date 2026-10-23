// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Threading;
using dnSpy.Roslyn.EditorFeatures.Extensions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Indentation;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Roslyn.Utilities;

namespace dnSpy.Roslyn.EditorFeatures.SmartIndent
{
    internal partial class SmartIndent : ISmartIndent
    {
        private readonly ITextView _textView;

        public SmartIndent(ITextView textView)
        {
            _textView = textView ?? throw new ArgumentNullException(nameof(textView));
        }

        public int? GetDesiredIndentation(ITextSnapshotLine line)
        {
            return GetDesiredIndentation(line, CancellationToken.None);
        }

        public void Dispose()
        {
        }

        private int? GetDesiredIndentation(ITextSnapshotLine lineToBeIndented, CancellationToken cancellationToken)
        {
            if (lineToBeIndented == null)
            {
                throw new ArgumentNullException(@"line");
            }

            //using (Logger.LogBlock(FunctionId.SmartIndentation_Start, cancellationToken))
            {
                var document = lineToBeIndented.Snapshot.GetOpenDocumentInCurrentContextWithChanges();
                var service = document?.GetLanguageService<IIndentationService>();
                if (service == null)
                {
                    return null;
                }

                var parsedDocument = ParsedDocument.CreateSynchronously(document, cancellationToken);
                var formattingOptions = document.GetSyntaxFormattingOptionsAsync(cancellationToken).AsTask().WaitAndGetResult(cancellationToken);
                var result = service.GetIndentation(parsedDocument, lineToBeIndented.LineNumber, new IndentationOptions(formattingOptions), cancellationToken);
                return result.GetIndentation(_textView, lineToBeIndented);
            }
        }
    }
}
