/*
 * Copyright 2013 ThirdMotion, Inc.
 *
 *	Licensed under the Apache License, Version 2.0 (the "License");
 *	you may not use this file except in compliance with the License.
 *	You may obtain a copy of the License at
 *
 *		http://www.apache.org/licenses/LICENSE-2.0
 *
 *		Unless required by applicable law or agreed to in writing, software
 *		distributed under the License is distributed on an "AS IS" BASIS,
 *		WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 *		See the License for the specific language governing permissions and
 *		limitations under the License.
 */

/**
 * @class strange.extensions.dispatcher.eventdispatcher.impl.EventDispatcher
 * 
 * A Dispatcher that uses IEvent to send messages.
 * 
 * Whenever the Dispatcher executes a `Dispatch()`, observers will be 
 * notified of any event (Key) for which they have registered.
 * 
 * EventDispatcher dispatches TmEvent : IEvent.
 * 
 * The EventDispatcher is the only Dispatcher currently released with Strange
 * (though by separating EventDispatcher from Dispatcher I'm obviously
 * signalling that I don't think it's the only possible one).
 * 
 * EventDispatcher is both an ITriggerProvider and an ITriggerable.
 * 
 * @see strange.extensions.dispatcher.eventdispatcher.api.IEvent
 * @see strange.extensions.dispatcher.api.ITriggerProvider
 * @see strange.extensions.dispatcher.api.ITriggerable
 */

using System;
using System.Collections.Generic;
using strange.framework.api;
using strange.framework.impl;
using strange.extensions.dispatcher.api;
using strange.extensions.dispatcher.eventdispatcher.api;
using strange.extensions.pool.api;
using strange.extensions.pool.impl;

namespace strange.extensions.dispatcher.eventdispatcher.impl
{
	public class EventDispatcher : Binder, IEventDispatcher, ITriggerProvider, ITriggerable
	{
		/// The list of clients that will be triggered as a consequence of an Event firing.
		protected HashSet<ITriggerable> triggerClients;
		protected HashSet<ITriggerable> triggerClientRemovals;
		protected bool isTriggeringClients;

		protected IPool<TmEvent> eventPool;

		public EventDispatcher ()
		{
			eventPool = new Pool<TmEvent> ();
			eventPool.InstanceProvider = new EventInstanceProvider ();
		}

		override public IBinding GetRawBinding()
		{
			return new EventBinding (resolver);
		}

		new public IEventBinding Bind(object key)
		{
			return base.Bind (key) as IEventBinding;
		}

		public void Dispatch (object eventType)
		{
			Dispatch (eventType, null);
		}

		public void Dispatch (object eventType, object data)
		{
			//Scrub the data to make eventType and data conform if possible
			data = conformDataToEvent (eventType, data);

			bool continueDispatch = true;
			if (triggerClients != null)
			{
				isTriggeringClients = true;
				foreach (ITriggerable trigger in triggerClients)
				{
					if (!trigger.Trigger(eventType, data))
					{
						continueDispatch = false;
						break;
					}
				}
				if (triggerClientRemovals != null)
				{
					flushRemovals();
				}
				isTriggeringClients = false;
			}

			if (!continueDispatch)
				return;

			IEventBinding binding = GetBinding (eventType) as IEventBinding;
			if (binding == null)
			{
				return;
			}

			object[] callbacks = (binding.value as object[]).Clone() as object[];
			if (callbacks == null)
				return;
			for(int a = 0; a < callbacks.Length; a++)
			{
				object callback = callbacks[a];
				if(callback == null)
					continue;

				callbacks[a] = null;

				object[] currentCallbacks = binding.value as object[];
				if(Array.IndexOf(currentCallbacks, callback) == -1)
					continue;

				if (callback is EventCallback)
				{
					invokeEventCallback (data, callback as EventCallback);
				}
				else if (callback is EmptyCallback)
				{
					(callback as EmptyCallback)();
				}
			}
			eventPool.ReturnInstance (data);
		}

		virtual protected object conformDataToEvent(object eventType, object data)
		{
			if (eventType == null)
			{
				throw new EventDispatcherException("Attempt to Dispatch to null.\ndata: " + data, EventDispatcherExceptionType.EVENT_KEY_NULL);
			}
			else if (eventType is IEvent)
			{
				//Client provided a full-formed event
				data = eventType;
				eventType = (data as IEvent).type;
			}
			else if (data == null)
			{
				//Client provided just an event ID. Create an event for injection
				data = createEvent (eventType, null);
			}
			else if (data is IEvent)
			{
				//Client provided both an evertType and a full-formed IEvent
				(data as IEvent).type = eventType;
			}
			else
			{
				//Client provided an eventType and some data which is not a IEvent.
				data = createEvent (eventType, data);
			}
			return data;
		}

		virtual protected object createEvent(object eventType, object data)
		{
			TmEvent retv = eventPool.GetInstance();
			retv.type = eventType;
			retv.target = this;
			retv.data = data;
			return retv;

		}

		virtual protected void invokeEventCallback(object data, EventCallback callback)
		{
			try
			{
				callback (data as IEvent);
			}
			catch(InvalidCastException)
			{
				object tgt = callback.Target;
				string methodName = (callback as Delegate).Method.Name;
				string message = "An EventCallback is attempting an illegal cast. One possible reason is not typing the payload to IEvent in your callback. Another is illegal casting of the data.\nTarget class: "  + tgt + " method: " + methodName;
				throw new EventDispatcherException (message, EventDispatcherExceptionType.TARGET_INVOCATION);
			}
		}

		public void AddListener(object evt, EventCallback callback)
		{
			IBinding binding = GetBinding (evt);
			if (binding == null)
			{
				Bind (evt).To (callback);
			}
			else
			{
				binding.To (callback);
			}
		}

		public void AddListener(object evt, EmptyCallback callback)
		{
			IBinding binding = GetBinding (evt);
			if (binding == null)
			{
				Bind (evt).To (callback);
			}
			else
			{
				binding.To (callback);
			}
		}

		public void RemoveListener(object evt, EventCallback callback)
		{
			IBinding binding = GetBinding (evt);
			RemoveValue (binding, callback);
		}

		public void RemoveListener(object evt, EmptyCallback callback)
		{
			IBinding binding = GetBinding (evt);
			RemoveValue (binding, callback);
		}

		public bool HasListener(object evt, EventCallback callback)
		{
			IEventBinding binding = GetBinding (evt) as IEventBinding;
			if (binding == null)
			{
				return false;
			}
			return binding.typeForCallback (callback) != EventCallbackType.NOT_FOUND;
		}

		public bool HasListener(object evt, EmptyCallback callback)
		{
			IEventBinding binding = GetBinding (evt) as IEventBinding;
			if (binding == null)
			{
				return false;
			}
			return binding.typeForCallback (callback) != EventCallbackType.NOT_FOUND;
		}

		public void UpdateListener(bool toAdd, object evt, EventCallback callback)
		{
			if (toAdd)
			{
				AddListener (evt, callback);
			}
			else
			{
				RemoveListener (evt, callback);
			}
		}

		public void UpdateListener(bool toAdd, object evt, EmptyCallback callback)
		{
			if (toAdd)
			{
				AddListener (evt, callback);
			}
			else
			{
				RemoveListener (evt, callback);
			}
		}

		public void AddTriggerable(ITriggerable target)
		{
			if (triggerClients == null)
			{
				triggerClients = new HashSet<ITriggerable>();
			}
			triggerClients.Add(target);
		}

		public void RemoveTriggerable(ITriggerable target)
		{
			if (triggerClients.Contains(target))
			{
				if (triggerClientRemovals == null)
				{
					triggerClientRemovals = new HashSet<ITriggerable>();
				}
				triggerClientRemovals.Add (target);
				if (!isTriggeringClients)
				{
					flushRemovals();
				}
			}
		}

		public int Triggerables
		{
			get
			{
				if (triggerClients == null)
					return 0;
				return triggerClients.Count;
			}
		}

		protected void flushRemovals()
		{
			if (triggerClientRemovals == null)
			{
				return;
			}
			foreach(ITriggerable target in triggerClientRemovals)
			{
				if (triggerClients.Contains(target))
				{
					triggerClients.Remove(target);
				}
			}
			triggerClientRemovals = null;
		}

		public bool Trigger<T>(object data)
		{
			return Trigger (typeof(T), data);
		}

		public bool Trigger(object key, object data)
		{
			Dispatch(key, data);
			return true;
		}
	}

	class EventInstanceProvider : IInstanceProvider
	{
		public T GetInstance<T>()
		{
			object instance = new TmEvent ();
			T retv = (T) instance;
			return retv;
		}

		public object GetInstance(Type key)
		{
			return new TmEvent ();
		}
	}
}
