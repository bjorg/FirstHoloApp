using System;
using System.Diagnostics;

namespace FirstHoloApp.Common {

    /// <summary>
    /// Helper class for animation and simulation timing.
    /// </summary>
    internal class StepTimer {

        // Integer format represents time using 10,000,000 ticks per second.
        private const long TICKS_PER_SECOND = 10000000;

        // Source timing data uses QPC units.
        private readonly long _qpcFrequency;
        private long _qpcLastTime;
        private readonly long _qpcMaxDelta;

        // Derived timing data uses a canonical tick format.
        private long _elapsedTicks;
        private long _totalTicks;
        private long _leftOverTicks;

        // Members for tracking the framerate.
        private int _frameCount;
        private int _framesPerSecond;
        private int _framesThisSecond;
        private long _qpcSecondCounter;

        // Members for configuring fixed timestep mode.
        private bool _isFixedTimeStep;
        private long _targetElapsedTicks = TICKS_PER_SECOND / 60;

        public StepTimer() {
            _qpcFrequency = Stopwatch.Frequency;
            _qpcLastTime = Stopwatch.GetTimestamp();

            // Initialize max delta to 1/10 of a second.
            _qpcMaxDelta = _qpcFrequency / 10;
        }

        /// <summary>
        /// After an intentional timing discontinuity (for instance a blocking IO operation)
        /// call this to avoid having the fixed timestep logic attempt a set of catch-up
        /// Update calls.
        /// </summary>
        public void ResetElapsedTime() {
            _qpcLastTime = Stopwatch.GetTimestamp();

            _leftOverTicks = 0;
            _framesPerSecond = 0;
            _framesThisSecond = 0;
            _qpcSecondCounter = 0;
        }

        /// <summary>
        /// Get elapsed time since the previous Update call.
        /// </summary>
        public long ElapsedTicks => _elapsedTicks;

        /// <summary>
        /// Get elapsed time since the previous Update call.
        /// </summary>
        public double ElapsedSeconds => TicksToSeconds(_elapsedTicks);

        /// <summary>
        /// Get total time since the start of the program.
        /// </summary>
        public long TotalTicks => _totalTicks;

        /// <summary>
        /// Get total time since the start of the program.
        /// </summary>
        public double TotalSeconds => TicksToSeconds(_totalTicks);

        /// <summary>
        /// Get total number of updates since start of the program.
        /// </summary>
        public int FrameCount => _frameCount;

        /// <summary>
        /// Get the current framerate.
        /// </summary>
        public int FramesPerSecond => _framesPerSecond;

        /// <summary>
        /// Get/Set whether to use fixed or variable timestep mode.
        /// </summary>
        public bool IsFixedTimeStep {
            get { return _isFixedTimeStep; }
            set { _isFixedTimeStep = value; }
        }

        /// <summary>
        /// Get/Set how often to call Update when in fixed timestep mode.
        /// </summary>
        public long TargetElapsedTicks {
            get { return _targetElapsedTicks; }
            set { _targetElapsedTicks = value; }
        }

        /// <summary>
        /// Get/Set how often to call Update when in fixed timestep mode.
        /// </summary>
        public double TargetElapsedSeconds {
            get { return TicksToSeconds(_targetElapsedTicks); }
            set { _targetElapsedTicks = SecondsToTicks(value); }
        }

        /// <summary>
        /// // Update timer state, calling the specified Update function the appropriate number of times.
        /// </summary>
        public void Tick(Action update) {
            // Query the current time.
            var currentTime = Stopwatch.GetTimestamp();

            var timeDelta = currentTime - _qpcLastTime;

            _qpcLastTime = currentTime;
            _qpcSecondCounter += timeDelta;

            // Clamp excessively large time deltas (e.g. after paused in the debugger).
            if(timeDelta > _qpcMaxDelta) {
                timeDelta = _qpcMaxDelta;
            }

            // Convert QPC units into a canonical tick format. This cannot overflow due to the previous clamp.
            timeDelta *= TICKS_PER_SECOND;
            timeDelta /= _qpcFrequency;

            long lastFrameCount = _frameCount;

            if(_isFixedTimeStep) {
                // Fixed timestep update logic

                // If the app is running very close to the target elapsed time (within 1/4 of a millisecond) just clamp
                // the clock to exactly match the target value. This prevents tiny and irrelevant errors
                // from accumulating over time. Without this clamping, a game that requested a 60 fps
                // fixed update, running with vsync enabled on a 59.94 NTSC display, would eventually
                // accumulate enough tiny errors that it would drop a frame. It is better to just round
                // small deviations down to zero to leave things running smoothly.

                if(Math.Abs(timeDelta - _targetElapsedTicks) < (TICKS_PER_SECOND / 4000)) {
                    timeDelta = _targetElapsedTicks;
                }

                _leftOverTicks += timeDelta;

                while(_leftOverTicks >= _targetElapsedTicks) {
                    _elapsedTicks = _targetElapsedTicks;
                    _totalTicks += _targetElapsedTicks;
                    _leftOverTicks -= _targetElapsedTicks;
                    _frameCount++;

                    update();
                }
            } else {
                // Variable timestep update logic.
                _elapsedTicks = timeDelta;
                _totalTicks += timeDelta;
                _leftOverTicks = 0;
                _frameCount++;

                update();
            }

            // Track the current framerate.
            if(_frameCount != lastFrameCount) {
                _framesThisSecond++;
            }

            if(_qpcSecondCounter >= _qpcFrequency) {
                _framesPerSecond = _framesThisSecond;
                _framesThisSecond = 0;
                _qpcSecondCounter %= _qpcFrequency;
            }
        }

        private static double TicksToSeconds(long ticks) {
            return (double)ticks / TICKS_PER_SECOND;
        }

        private static long SecondsToTicks(double seconds) {
            return (long)(seconds * TICKS_PER_SECOND);
        }
    }
}
