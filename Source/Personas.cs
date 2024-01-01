﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace RimGPT
{
	public static class Personas
	{
		public static readonly int maxQueueSize = 3;
		public static bool IsAudioQueueFull => speechQueue.Count >= maxQueueSize;
		public static ConcurrentQueue<SpeechJob> speechQueue = new();
		public static string currentText = "";
		public static Persona currentSpeakingPersona = null;
		private static OrderedHashSet<Phrase> allPhrases = new OrderedHashSet<Phrase>();
		public static Persona lastSpeakingPersona = null;

		static Personas()
		{
			StartNextPersona();
			Tools.SafeLoop(() =>
			{
				foreach (var persona in RimGPTMod.Settings.personas)
					persona.Periodic();
			},
			1000);

			Tools.SafeLoop(async () =>
			{
				if (speechQueue.TryPeek(out var job) == false || job.completed == false)
				{
					//FileLog.Log($"...{(job == null ? "empty" : "job")}");
					await Tools.SafeWait(200);
					return true;
				}
				_ = speechQueue.TryDequeue(out job);
				//FileLog.Log($"SPEECH QUEUE -1 => {speechQueue.Count} -> play {job.persona?.name ?? "null"}: {job.audioClip != null}/{job.completed}");
				await job.Play(false);
				return false;
			});
		}
		public static void StartNextPersona(Persona lastSpeaker = null)
		{
			lock (allPhrases)
			{

				// Chroniclers get all phrases regardless
				foreach (var chronicler in RimGPTMod.Settings.personas.Where(p => p.isChronicler))
				{
					// Transfer all available phrases to each chronicler
					foreach (var phrase in allPhrases)
					{
						if (!chronicler.phrases.Contains(phrase)) chronicler.phrases.Add(phrase);
					}
				}

				var candidates = RimGPTMod.Settings.personas.Where(p => p.nextPhraseTime > DateTime.Now && !p.isChronicler);
				lastSpeakingPersona = lastSpeaker;
				Persona nextPersona;

				if (!candidates.Any())
				{
					// If there are no future phrase times, simply use the round-robin approach.
					int currentIndex = lastSpeaker != null ? RimGPTMod.Settings.personas.IndexOf(lastSpeaker) : 0;
					if (currentIndex == -1 || currentIndex >= RimGPTMod.Settings.personas.Count - 1)
						currentIndex = 0;
					else
						currentIndex++;

					nextPersona = RimGPTMod.Settings.personas[currentIndex];
				}
				else
				{

					nextPersona = candidates.OrderBy(p => p.nextPhraseTime).First();
				}


				int transferCount = Math.Min(nextPersona.phrasesLimit, allPhrases.Count);

				for (int i = 0; i < transferCount; i++)
				{

					if (!nextPersona.phrases.Contains(allPhrases[i]))
					{
						nextPersona.phrases.Add(allPhrases[i]);
					}
				}

				// add the high priority ones to all personas last, so its most recent
				// we want to ensure they're aware that the hunter has his weapon ffs
				var highPriorityPhrases = allPhrases.Where(p => p.priority >= 4).ToList();
				foreach (var persona in RimGPTMod.Settings.personas)
				{
					if (persona.isChronicler) break;

					foreach (var phrase in highPriorityPhrases)
					{
						if (!persona.phrases.Contains(phrase)) persona.phrases.Add(phrase);
					}
				}

				// Remove transferred phrases from the beginning of the list
				allPhrases.RemoveFromStart(transferCount);

				// Add last spoken phrase from the previous speaker if it's not null
				if (lastSpeaker != null)
				{
					var lastSpokenPhrase = new Phrase
					{
						text = $"{lastSpeaker.name} said, \"{lastSpeaker.lastSpokenText}\"",
						persona = lastSpeaker,
						priority = 3
					};
					if (!nextPersona.phrases.Contains(lastSpokenPhrase)) nextPersona.phrases.Add(lastSpokenPhrase);

				}
			}
		}

		public static bool IsAnyCompletedJobWaiting()
		{
			return speechQueue.Any(job => job.readyForNextJob && !job.isPlaying);
		}

		public static void Add(string text, int priority, Persona speaker = null)
		{
			var phrase = new Phrase(speaker, text, priority);
			Logger.Message(phrase.ToString());
			lock (allPhrases)
			{
				if (allPhrases.Contains(phrase)) return;

				allPhrases.Add(phrase);
			}
		}


		public static void RemoveSpeechDelayForPersona(Persona persona)
		{
			lock (speechQueue)
			{
				foreach (var job in speechQueue)
					if (job.persona == persona && job.doneCallback != null)
					{
						var callback = job.doneCallback;
						job.doneCallback = null;
						callback();
					}
			}
		}

		public static void Reset(params string[] reason)
		{
			lock (speechQueue)
			{
				speechQueue.Clear();
				foreach (var persona in RimGPTMod.Settings.personas)
					persona.Reset(reason);
			}
		}

		public static void CreateSpeechJob(Persona persona, Phrase[] phrases, Action<string> errorCallback, Action doneCallback)
		{

			lock (speechQueue)
			{
				if (IsAudioQueueFull == false && RimGPTMod.Settings.azureSpeechKey != "" && RimGPTMod.Settings.azureSpeechRegion != "")
				{
					var filteredPhrases = phrases.Where(obs => obs.persona?.name != persona.name).ToArray();
					var job = new SpeechJob(persona, filteredPhrases, errorCallback, doneCallback);
					speechQueue.Enqueue(job);
					//FileLog.Log($"SPEECH QUEUE +1 => {speechQueue.Count}");
				}
				else
					doneCallback();
			}
		}

		public static void UpdateVoiceInformation()
		{
			TTS.voices = new Voice[0];
			if (RimGPTMod.Settings.azureSpeechKey == "" || RimGPTMod.Settings.azureSpeechRegion == "")
				return;

			Tools.SafeAsync(async () =>
			{
				TTS.voices = await TTS.DispatchFormPost<Voice[]>($"{TTS.APIURL}/voices/list", null, true, null);
				foreach (var persona in RimGPTMod.Settings.personas)
				{
					var voiceLanguage = Tools.VoiceLanguage(persona);
					var currentVoice = Voice.From(persona.azureVoice);
					if (currentVoice != null && currentVoice.LocaleName.Contains(voiceLanguage) == false)
					{
						currentVoice = TTS.voices
							.Where(voice => voice.LocaleName.Contains(voiceLanguage))
							.OrderBy(voice => voice.DisplayName)
							.FirstOrDefault();
						persona.azureVoice = currentVoice?.ShortName ?? "";
						persona.azureVoiceStyle = "default";
					}
				}
			});
		}
	}
}