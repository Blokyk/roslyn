﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
#if DEBUG
using System.Diagnostics;
#endif
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Editor.Shared.Extensions;
using Microsoft.CodeAnalysis.Editor.Shared.Options;
using Microsoft.CodeAnalysis.Editor.Shared.Utilities;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Shared.TestHooks;
using Microsoft.CodeAnalysis.Text.Shared.Extensions;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.Editor.Tagging
{
    /// <summary>
    /// Base type of all asynchronous tagger providers (<see cref="ITaggerProvider"/> and <see cref="IViewTaggerProvider"/>). 
    /// </summary>
    internal abstract partial class AbstractAsynchronousTaggerProvider<TTag> : ForegroundThreadAffinitizedObject where TTag : ITag
    {
        private readonly object _uniqueKey = new();
        private readonly IForegroundNotificationService _notificationService;

        protected readonly IAsynchronousOperationListener AsyncListener;

        /// <summary>
        /// The behavior the tagger engine will have when text changes happen to the subject buffer
        /// it is attached to.  Most taggers can simply use <see cref="TaggerTextChangeBehavior.None"/>.
        /// However, advanced taggers that want to perform specialized behavior depending on what has
        /// actually changed in the file can specify <see cref="TaggerTextChangeBehavior.TrackTextChanges"/>.
        /// 
        /// If this is specified the tagger engine will track text changes and pass them along as
        /// <see cref="TaggerContext{TTag}.TextChangeRange"/> when calling 
        /// <see cref="ProduceTagsAsync(TaggerContext{TTag})"/>.
        /// </summary>
        protected virtual TaggerTextChangeBehavior TextChangeBehavior => TaggerTextChangeBehavior.None;

        /// <summary>
        /// The behavior the tagger will have when changes happen to the caret.
        /// </summary>
        protected virtual TaggerCaretChangeBehavior CaretChangeBehavior => TaggerCaretChangeBehavior.None;

        /// <summary>
        /// The behavior of tags that are created by the async tagger.  This will matter for tags
        /// created for a previous version of a document that are mapped forward by the async
        /// tagging architecture.  This value cannot be <see cref="SpanTrackingMode.Custom"/>.
        /// </summary>
        protected virtual SpanTrackingMode SpanTrackingMode => SpanTrackingMode.EdgeExclusive;

        /// <summary>
        /// Options controlling this tagger.  The tagger infrastructure will check this option
        /// against the buffer it is associated with to see if it should tag or not.
        /// 
        /// An empty enumerable, or null, can be returned to indicate that this tagger should 
        /// run unconditionally.
        /// </summary>
        protected virtual IEnumerable<Option2<bool>> Options => SpecializedCollections.EmptyEnumerable<Option2<bool>>();
        protected virtual IEnumerable<PerLanguageOption2<bool>> PerLanguageOptions => SpecializedCollections.EmptyEnumerable<PerLanguageOption2<bool>>();

        /// <summary>
        /// This controls what delay tagger will use to let editor know about newly inserted tags
        /// </summary>
        protected virtual TaggerDelay AddedTagNotificationDelay => TaggerDelay.NearImmediate;

#if DEBUG
        public readonly string StackTrace;
#endif

        protected AbstractAsynchronousTaggerProvider(
            IThreadingContext threadingContext,
            IAsynchronousOperationListener asyncListener,
            IForegroundNotificationService notificationService)
            : base(threadingContext)
        {
            AsyncListener = asyncListener;
            _notificationService = notificationService;

#if DEBUG
            StackTrace = new StackTrace().ToString();
#endif
        }

        internal IAccurateTagger<T>? CreateTaggerWorker<T>(ITextView textViewOpt, ITextBuffer subjectBuffer) where T : ITag
        {
            if (!subjectBuffer.GetFeatureOnOffOption(EditorComponentOnOffOptions.Tagger))
            {
                return null;
            }

            var tagSource = GetOrCreateTagSource(textViewOpt, subjectBuffer);
            return new Tagger(ThreadingContext, AsyncListener, _notificationService, tagSource, subjectBuffer) as IAccurateTagger<T>;
        }

        private TagSource GetOrCreateTagSource(ITextView textViewOpt, ITextBuffer subjectBuffer)
        {
            if (!this.TryRetrieveTagSource(textViewOpt, subjectBuffer, out var tagSource))
            {
                tagSource = new TagSource(textViewOpt, subjectBuffer, this, AsyncListener, _notificationService);

                this.StoreTagSource(textViewOpt, subjectBuffer, tagSource);
                tagSource.Disposed += (s, e) => this.RemoveTagSource(textViewOpt, subjectBuffer);
            }

            return tagSource;
        }

        private bool TryRetrieveTagSource(ITextView textViewOpt, ITextBuffer subjectBuffer, [NotNullWhen(true)] out TagSource? tagSource)
        {
            return textViewOpt != null
                ? textViewOpt.TryGetPerSubjectBufferProperty(subjectBuffer, _uniqueKey, out tagSource)
                : subjectBuffer.Properties.TryGetProperty(_uniqueKey, out tagSource);
        }

        private void RemoveTagSource(ITextView textViewOpt, ITextBuffer subjectBuffer)
        {
            if (textViewOpt != null)
            {
                textViewOpt.RemovePerSubjectBufferProperty<TagSource, ITextView>(subjectBuffer, _uniqueKey);
            }
            else
            {
                subjectBuffer.Properties.RemoveProperty(_uniqueKey);
            }
        }

        private void StoreTagSource(ITextView textViewOpt, ITextBuffer subjectBuffer, TagSource tagSource)
        {
            if (textViewOpt != null)
            {
                textViewOpt.AddPerSubjectBufferProperty(subjectBuffer, _uniqueKey, tagSource);
            }
            else
            {
                subjectBuffer.Properties.AddProperty(_uniqueKey, tagSource);
            }
        }

        /// <summary>
        /// Called by the <see cref="AbstractAsynchronousTaggerProvider{TTag}"/> infrastructure to 
        /// determine the caret position.  This value will be passed in as the value to 
        /// <see cref="TaggerContext{TTag}.CaretPosition"/> in the call to
        /// <see cref="ProduceTagsAsync(TaggerContext{TTag})"/>.
        /// </summary>
        protected virtual SnapshotPoint? GetCaretPoint(ITextView textViewOpt, ITextBuffer subjectBuffer)
            => textViewOpt?.GetCaretPoint(subjectBuffer);

        /// <summary>
        /// Called by the <see cref="AbstractAsynchronousTaggerProvider{TTag}"/> infrastructure to determine
        /// the set of spans that it should asynchronously tag.  This will be called in response to
        /// notifications from the <see cref="ITaggerEventSource"/> that something has changed, and
        /// will only be called from the UI thread.  The tagger infrastructure will then determine
        /// the <see cref="DocumentSnapshotSpan"/>s associated with these <see cref="SnapshotSpan"/>s
        /// and will asynchronously call into <see cref="ProduceTagsAsync(TaggerContext{TTag})"/> at some point in
        /// the future to produce tags for these spans.
        /// </summary>
        protected virtual IEnumerable<SnapshotSpan> GetSpansToTag(ITextView textViewOpt, ITextBuffer subjectBuffer)
        {
            // For a standard tagger, the spans to tag is the span of the entire snapshot.
            return SpecializedCollections.SingletonEnumerable(subjectBuffer.CurrentSnapshot.GetFullSpan());
        }

        /// <summary>
        /// Creates the <see cref="ITaggerEventSource"/> that notifies the <see cref="AbstractAsynchronousTaggerProvider{TTag}"/>
        /// that it should recompute tags for the text buffer after an appropriate <see cref="TaggerDelay"/>.
        /// </summary>
        protected abstract ITaggerEventSource CreateEventSource(ITextView textViewOpt, ITextBuffer subjectBuffer);

        /// <summary>
        /// Produce tags for the given context.
        /// Keep in sync with <see cref="ProduceTagsSynchronously(TaggerContext{TTag})"/>
        /// </summary>
        protected virtual async Task ProduceTagsAsync(TaggerContext<TTag> context)
        {
            foreach (var spanToTag in context.SpansToTag)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                await ProduceTagsAsync(
                    context, spanToTag,
                    GetCaretPosition(context.CaretPosition, spanToTag.SnapshotSpan)).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Produce tags for the given context.
        /// Keep in sync with <see cref="ProduceTagsAsync(TaggerContext{TTag})"/>
        /// </summary>
        protected void ProduceTagsSynchronously(TaggerContext<TTag> context)
        {
            foreach (var spanToTag in context.SpansToTag)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                ProduceTagsSynchronously(
                    context, spanToTag,
                    GetCaretPosition(context.CaretPosition, spanToTag.SnapshotSpan));
            }
        }

        private static int? GetCaretPosition(SnapshotPoint? caretPosition, SnapshotSpan snapshotSpan)
        {
            return caretPosition.HasValue && caretPosition.Value.Snapshot == snapshotSpan.Snapshot
                ? caretPosition.Value.Position : (int?)null;
        }

        protected virtual Task ProduceTagsAsync(TaggerContext<TTag> context, DocumentSnapshotSpan spanToTag, int? caretPosition)
            => Task.CompletedTask;

        protected virtual void ProduceTagsSynchronously(TaggerContext<TTag> context, DocumentSnapshotSpan spanToTag, int? caretPosition)
        {
            // By default we implement the sync version of this by blocking on the async version.
            //
            // The benefit of this is that all taggers can implicitly be used as IAccurateTaggers
            // without any code changes.
            // 
            // However, the drawback is that it means the UI thread might be blocked waiting for 
            // tasks to be scheduled and run on the threadpool. 
            //
            // Taggers that need to be called accurately should override this method to produce
            // results quickly if possible.
            ProduceTagsAsync(context, spanToTag, caretPosition).Wait(context.CancellationToken);
        }

        internal TestAccessor GetTestAccessor()
            => new(this);

        private struct DiffResult
        {
            public NormalizedSnapshotSpanCollection Added { get; }
            public NormalizedSnapshotSpanCollection Removed { get; }

            public DiffResult(List<SnapshotSpan> added, List<SnapshotSpan> removed)
                : this(added?.Count == 0 ? null : (IEnumerable<SnapshotSpan>?)added, removed?.Count == 0 ? null : (IEnumerable<SnapshotSpan>?)removed)
            {
            }

            public DiffResult(IEnumerable<SnapshotSpan>? added, IEnumerable<SnapshotSpan>? removed)
            {
                Added = added != null ? new NormalizedSnapshotSpanCollection(added) : NormalizedSnapshotSpanCollection.Empty;
                Removed = removed != null ? new NormalizedSnapshotSpanCollection(removed) : NormalizedSnapshotSpanCollection.Empty;
            }

            public int Count => Added.Count + Removed.Count;
        }

        internal readonly struct TestAccessor
        {
            private readonly AbstractAsynchronousTaggerProvider<TTag> _provider;

            public TestAccessor(AbstractAsynchronousTaggerProvider<TTag> provider)
                => _provider = provider;

            internal Task ProduceTagsAsync(TaggerContext<TTag> context)
                => _provider.ProduceTagsAsync(context);
        }
    }
}
