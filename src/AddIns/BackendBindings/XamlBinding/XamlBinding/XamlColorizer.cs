// <file>
//     <copyright see="prj:///doc/copyright.txt"/>
//     <license see="prj:///doc/license.txt"/>
//     <owner name="Siegfried Pammer" email="sie_pam@gmx.at"/>
//     <version>$Revision$</version>
// </file>

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Dom;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.XmlEditor;
using System.Windows.Threading;

namespace ICSharpCode.XamlBinding
{
	using Tasks = System.Threading.Tasks;
	
	public class XamlColorizer : DocumentColorizingTransformer, ILineTracker
	{
		static readonly XamlColorizerSettings defaultSettings = new XamlColorizerSettings();
		
		public struct Highlight {
			public IMember Member { get; set; }
			public HighlightingInfo Info { get; set; }
		}
		
		public sealed class HighlightTask {
			// input
			public string FileContent { get; private set; }
			public string FileName { get; private set; }
			public string LineText { get; private set; }
			public int LineNumber { get; private set; }
			public int Offset { get; private set; }
			
			TextView textView;
			
			public HighlightTask(string fileContent, string fileName, DocumentLine currentLine, TextView textView)
			{
				this.FileContent = fileContent;
				this.FileName = fileName;
				this.task = new System.Threading.Tasks.Task(Process);
				this.textView = textView;
				this.LineNumber = currentLine.LineNumber;
				this.LineText = currentLine.Text;
				this.Offset = currentLine.Offset;
			}
			
			IList<Highlight> results;
			
			// output
			public IList<Highlight> GetResults()
			{
				return results;
			}
			
			Tasks.Task task;
			
			public void Start()
			{
				task.Start();
			}
			
			public bool CompletedSuccessfully {
				get {
					return task.IsCompleted && task.Status == Tasks.TaskStatus.RanToCompletion && !Invalid;
				}
			}
			
			public bool Invalid { get; set; }
			
			public void Invalidate(string fileContent, string fileName, DocumentLine currentLine, TextView textView)
			{
				task.Cancel();
				this.Invalid = false;
				
				this.FileContent = fileContent;
				this.FileName = fileName;
				this.textView = textView;
				
				this.LineNumber = currentLine.LineNumber;
				this.LineText = currentLine.Text;
				this.Offset = currentLine.Offset;
				
				this.task = new System.Threading.Tasks.Task(Process);
				this.task.Start();
			}
			
			void Process()
			{
				List<Highlight> results = new List<Highlight>();
				
				foreach (HighlightingInfo info in GetInfo()) {
					IMember member = null;
					
					if (task.IsCancellationRequested)
						return;
					
					if (!info.Token.StartsWith("xmlns")) {
						MemberResolveResult rr = new XamlResolver().Resolve(info.GetExpressionResult(), info.Context.ParseInformation, FileContent) as MemberResolveResult;
						member = (rr != null) ? rr.ResolvedMember : null;
					}
					
					results.Add(new Highlight() { Member = member, Info = info });
				}
				
				lock (this) {
					this.results = results;
					this.Invalid = false;
				}
				
				WorkbenchSingleton.SafeThreadAsyncCall(InvokeRedraw);
			}
			
			void InvokeRedraw()
			{
				textView.Redraw(this.Offset, this.LineText.Length, DispatcherPriority.Background);
			}
			
			IEnumerable<HighlightingInfo> GetInfo()
			{
				int index = -1;
				XamlContext context = null;
				List<HighlightingInfo> infos = new List<HighlightingInfo>();
				
				do {
					if (index + 1 >= LineText.Length)
						break;

					index = LineText.IndexOfAny(index + 1, '=', '.');
					if (index > -1) {
						context = CompletionDataHelper.ResolveContext(FileContent, FileName, LineNumber, index);
						string elementName = context.ActiveElement.FullXmlName;
						int propertyNameIndex = elementName.IndexOf('.');
						string attribute = (context.AttributeName != null) ? context.AttributeName.FullXmlName : string.Empty;
						if (attribute.Contains(".")) {
							int tmp = attribute.IndexOf('.');
							index += attribute.Substring(tmp).Length;
						} else if (string.IsNullOrEmpty(attribute) && elementName.Contains(".")) {
							attribute = elementName;
							index += attribute.Substring(propertyNameIndex).Length;
						}
						if (context.Description != XamlContextDescription.InComment && !string.IsNullOrEmpty(attribute)) {
							int startIndex = LineText.Substring(0, Math.Min(index, LineText.Length)).LastIndexOf(attribute);
							if (propertyNameIndex > -1)
								infos.Add(new HighlightingInfo(attribute.Trim('/'), startIndex + propertyNameIndex + 1, startIndex + attribute.TrimEnd('/').Length, Offset, context));
							else
								infos.Add(new HighlightingInfo(attribute, startIndex, startIndex + attribute.Length, Offset, context));
						}
					}
				} while (index > -1);
				
				return infos;
			}
		}
		
		XamlColorizerSettings settings = defaultSettings;
		string fileContent;
		string fileName;
		IDocument document;
		
		Dictionary<DocumentLine, HighlightTask> highlightCache = new Dictionary<DocumentLine, HighlightTask>();
		
		public IViewContent Content { get; set; }
		
		public AvalonEdit.Rendering.TextView TextView { get; set;	}
		
		public XamlColorizer(IViewContent content, TextView textView)
		{
			this.Content = content;
			this.TextView = textView;
			
			IFileDocumentProvider documentProvider = this.Content as IFileDocumentProvider;
			
			if (documentProvider == null)
				throw new InvalidOperationException("XamlColorizer only works with ITextEditor view contents");
			
			this.document = documentProvider.GetDocumentForFile(this.Content.PrimaryFile);
			
			WeakLineTracker.Register(this.document.GetService(typeof(TextDocument)) as TextDocument, this);
		}
		
		protected override void Colorize(ITextRunConstructionContext context)
		{
			this.fileContent = this.document.CreateSnapshot().Text;
			this.fileName = this.Content.PrimaryFileName;
			
			base.Colorize(context);
		}
		
		protected override void ColorizeLine(DocumentLine line)
		{
			if (!highlightCache.ContainsKey(line)) {
				HighlightTask task = new HighlightTask(this.fileContent, this.fileName, line, this.TextView);
				task.Start();
				highlightCache.Add(line, task);
			} else {
				HighlightTask task = highlightCache[line];
				if (task.CompletedSuccessfully) {
					foreach (var result in task.GetResults()) {
						ColorizeMember(result.Info, line, result.Member);
					}
				}
			}
			ColorizeInvalidated();
		}
		
		void ColorizeMember(HighlightingInfo info, DocumentLine line, IMember member)
		{
			try {
				if (info.Context.IgnoredXmlns.Any(item => info.Token.StartsWith(item + ":"))) {
					ChangeLinePart(line.Offset + info.StartOffset, line.Offset + info.EndOffset, HighlightIgnored);
				} else {
					if (member != null) {
						if (member is IEvent)
							ChangeLinePart(line.Offset + info.StartOffset, line.Offset + info.EndOffset, HighlightEvent);
						else
							ChangeLinePart(line.Offset + info.StartOffset, line.Offset + info.EndOffset, HighlightProperty);
					} else {
						if (info.Token.StartsWith("xmlns") || info.Token.StartsWith(Utils.GetNamespacePrefix(CompletionDataHelper.MarkupCompatibilityNamespace, info.Context) + ":"))
							ChangeLinePart(line.Offset + info.StartOffset, line.Offset + info.EndOffset, HighlightNamespaceDeclaration);
						else
							Core.LoggingService.Debug(info.Token + " not highlighted; line " + line.LineNumber);
					}
				}
			} catch (ArgumentOutOfRangeException) {
				highlightCache.Remove(line);
			}
		}
		
		void ColorizeInvalidated()
		{
			foreach (var item in highlightCache) {
				if (item.Value.Invalid) {
					item.Value.Invalidate(this.fileContent, this.fileName, item.Key, this.TextView);
				}
			}
		}
				
		void InvalidateLines(DocumentLine line)
		{
			DocumentLine current = line;
			while (current != null) {
				HighlightTask task;
				if (highlightCache.TryGetValue(current, out task)) {
					task.Invalid = true;
				}

				current = current.NextLine;
			}
		}
		
		void HighlightProperty(VisualLineElement element)
		{
			element.TextRunProperties.SetForegroundBrush(settings.PropertyForegroundBrush);
			element.TextRunProperties.SetBackgroundBrush(settings.PropertyBackgroundBrush);
		}
		
		void HighlightEvent(VisualLineElement element)
		{
			element.TextRunProperties.SetForegroundBrush(settings.EventForegroundBrush);
			element.TextRunProperties.SetBackgroundBrush(settings.EventBackgroundBrush);
		}
		
		void HighlightNamespaceDeclaration(VisualLineElement element)
		{
			element.TextRunProperties.SetForegroundBrush(settings.NamespaceDeclarationForegroundBrush);
			element.TextRunProperties.SetBackgroundBrush(settings.NamespaceDeclarationBackgroundBrush);
		}
		
		void HighlightIgnored(VisualLineElement element)
		{
			element.TextRunProperties.SetForegroundBrush(settings.IgnoredForegroundBrush);
			element.TextRunProperties.SetBackgroundBrush(settings.IgnoredBackgroundBrush);
		}
		
		public void BeforeRemoveLine(DocumentLine line)
		{
			highlightCache.Remove(line);
			InvalidateLines(line.NextLine);
		}
		
		public void SetLineLength(DocumentLine line, int newTotalLength)
		{
			highlightCache.Remove(line);
			InvalidateLines(line.NextLine);
		}
		
		public void LineInserted(DocumentLine insertionPos, DocumentLine newLine)
		{
		}
		
		public void RebuildDocument()
		{
			highlightCache.Clear();
		}
		
		public struct HighlightingInfo
		{
			public static readonly HighlightingInfo Empty = new HighlightingInfo(string.Empty, 0, 0, 0, new XamlContext());
			
			string token;
			int startOffset;
			int endOffset;
			int lineOffset;
			XamlContext context;
			
			public HighlightingInfo(string token, int startOffset, int endOffset, int lineOffset, XamlContext context)
			{
				if (token == null)
					throw new ArgumentNullException("token");
				if (startOffset < 0)
					throw new ArgumentOutOfRangeException("startOffset", startOffset, "Value must be greater 0");
				if (endOffset < 0)
					throw new ArgumentOutOfRangeException("endOffset", endOffset, "Value must be greater 0");
				if (lineOffset < 0)
					throw new ArgumentOutOfRangeException("lineOffset", lineOffset, "Value must be greater 0");
				if (context == null)
					throw new ArgumentNullException("context");
				
				this.token = token;
				this.startOffset = startOffset;
				this.endOffset = endOffset;
				this.lineOffset = lineOffset;
				this.context = context;
			}
			
			public string Token {
				get { return token; }
			}
			
			public int StartOffset {
				get { return startOffset; }
			}
			
			public int EndOffset {
				get { return endOffset; }
			}
			
			public int LineOffset {
				get { return lineOffset; }
			}
			
			public XamlContext Context {
				get { return context; }
			}
			
			public ExpressionResult GetExpressionResult() {
				return new ExpressionResult(token, context);
			}
		}
	}
}