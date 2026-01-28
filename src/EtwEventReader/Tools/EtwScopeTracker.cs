//-----------------------------------------------------------------------
// <copyright file="EtwScopeTracker.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace EtwEventReader.Tools
{
    using System;
    using System.Collections.Generic;
    using Microsoft.Diagnostics.Tracing;

    /// <summary>
    /// Class to keep track of event scopes to pair start and stop events.
    /// </summary>
    /// <typeparam name="T">The type of the object which represents a wrapped event.</typeparam>
    public class EtwScopeTracker<T> where T : class
    {
        /// <summary>
        /// Keeps a list of all open scopes by event name.
        /// </summary>
        private readonly Dictionary<string, Stack<T>> openScopesToEventObjects = [];

        /// <summary>
        /// Indicates that a specific event name has started a scope and pushes that on the stack to await an end event.
        /// </summary>
        /// <param name="startEvent">The starting event.</param>
        /// <param name="startEventObject">The wrapped start event.</param>
        public void PushScope(TraceEvent startEvent, T startEventObject)
        {
            var uniqueEventName = EtwScopeTracker<T>.GetUniqueEventName(startEvent);

            if (!this.openScopesToEventObjects.TryGetValue(uniqueEventName, out var value))
            {
                value = new Stack<T>();
                this.openScopesToEventObjects[uniqueEventName] = value;
            }

            value.Push(startEventObject);
        }

        /// <summary>
        /// Retrieves the wrapped start event associated with this end event.
        /// </summary>
        /// <param name="endEvent">The end event.</param>
        /// <returns>The wrapped start event associated with this end event.</returns>
        public T? PopScope(TraceEvent endEvent)
        {
            var uniqueEventName = EtwScopeTracker<T>.GetUniqueEventName(endEvent);
            T? result;

            if (!this.openScopesToEventObjects.TryGetValue(uniqueEventName, out var value) || value.Count == 0)
            {
                result = default;
            }
            else
            {
                result = value.Pop();
            }

            return result;
        }

        /// <summary>
        /// Gets a unique name for an event.
        /// </summary>
        /// <param name="traceEvent">An event.</param>
        /// <returns>The unique name for this event.</returns>
        private static string GetUniqueEventName(TraceEvent traceEvent)
        {
            return traceEvent.ProviderName + "_" + traceEvent.TaskName + "_" + traceEvent.ProcessID + "_" + traceEvent.ActivityID;
        }
    }
}