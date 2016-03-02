﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace GitHub.Internals
{
    /// <summary>
    /// An instance of an experiment. This actually runs the control and the candidate and measures the result.
    /// </summary>
    /// <typeparam name="T">The return type of the experiment</typeparam>
    internal class ExperimentInstance<T>
    {
        internal const string CandidateExperimentName = "candidate";
        internal const string ControlExperimentName = "control";

        internal readonly string Name;
        internal readonly List<NamedBehavior> Behaviors;
        internal readonly Func<T, T, bool> Comparator;
        internal readonly Func<Task> BeforeRun;
        internal readonly Func<Task<bool>> RunIf;
        internal readonly IEnumerable<Func<T, T, Task<bool>>> Ignores;
        
        static Random _random = new Random(DateTimeOffset.UtcNow.Millisecond);

        public ExperimentInstance(string name, Func<Task<T>> control, Func<Task<T>> candidate, Func<T, T, bool> comparator, Func<Task> beforeRun, Func<Task<bool>> runIf, IEnumerable<Func<T, T, Task<bool>>> ignores)
            : this(name,
                  new NamedBehavior(ControlExperimentName, control),
                  new NamedBehavior(CandidateExperimentName, candidate),
                  comparator,
                  beforeRun,
                  runIf,
                  ignores)
        {
        }

        internal ExperimentInstance(string name, NamedBehavior control, NamedBehavior candidate, Func<T, T, bool> comparator, Func<Task> beforeRun, Func<Task<bool>> runIf, IEnumerable<Func<T, T, Task<bool>>> ignores)
        {
            Name = name;
            Behaviors = new List<NamedBehavior>
            {
                control,
                candidate
            };
            Comparator = comparator;
            BeforeRun = beforeRun;
            RunIf = runIf;
            Ignores = ignores;
        }

        public async Task<T> Run()
        {
            // Determine if experiments should be run.
            if (!await RunIf())
            {
                // Run the control behavior.
                return await Behaviors[0].Behavior();
            }

            if (BeforeRun != null)
            {
                await BeforeRun();
            }

            // Randomize ordering...
            NamedBehavior[] orderedBehaviors;
            lock (_random)
            {
                orderedBehaviors = Behaviors.OrderBy(b => _random.Next()).ToArray();
            }

            var observations = new List<Observation<T>>();
            foreach (var behavior in orderedBehaviors)
            {
                observations.Add(await Observation<T>.New(behavior.Name, behavior.Behavior, Comparator));
            }

            var controlObservation = observations.FirstOrDefault(o => o.Name == ControlExperimentName);
            
            var result = new Result<T>(this, observations, controlObservation);

            // TODO: Make this Fire and forget so we don't have to wait for this
            // to complete before we return a result
            await Scientist.ResultPublisher.Publish(result);

            if (controlObservation.Thrown) throw controlObservation.Exception;
            return controlObservation.Value;
        }

        public async Task<bool> IgnoreMismatchedObservation(Observation<T> control, Observation<T> candidate)
        {
            if (!Ignores.Any())
            {
                return false;
            }

            //TODO: Does this really need to be async? We could run sync and return on first true
            var results = await Task.WhenAll(Ignores.Select(i => i(control.Value, candidate.Value)));

            return results.Any(i => i);
        }
        
        internal class NamedBehavior
        {
            public NamedBehavior(string name, Func<T> behavior)
                : this(name, () => Task.FromResult(behavior()))
            {
            }

            public NamedBehavior(string name, Func<Task<T>> behavior)
            {
                Behavior = behavior;
                Name = name;
            }

            /// <summary>
            /// Gets the behavior to execute during an experiment.
            /// </summary>
            public Func<Task<T>> Behavior { get; }

            /// <summary>
            /// Gets the name of the behavior.
            /// </summary>
            public string Name { get; }
        }
    }
}