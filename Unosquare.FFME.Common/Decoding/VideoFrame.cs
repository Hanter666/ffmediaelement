﻿namespace Unosquare.FFME.Decoding
{
    using ClosedCaptions;
    using FFmpeg.AutoGen;
    using Shared;
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Represents a wrapper for an unmanaged ffmpeg video frame.
    /// </summary>
    /// <seealso cref="MediaFrame" />
    internal sealed unsafe class VideoFrame : MediaFrame
    {
        #region Private Members

        private readonly object DisposeLock = new object();
        private bool IsDisposed;

        #endregion

        #region Constructors

        /// <summary>
        /// Initializes a new instance of the <see cref="VideoFrame" /> class.
        /// </summary>
        /// <param name="frame">The frame.</param>
        /// <param name="component">The video component.</param>
        internal VideoFrame(AVFrame* frame, VideoComponent component)
            : base(frame, component, MediaType.Video)
        {
            var timeBase = ffmpeg.av_guess_frame_rate(component.Container.InputContext, component.Stream, frame);
            var repeatFactor = 1d + (0.5d * frame->repeat_pict);

            Duration = frame->pkt_duration <= 0 ?
                repeatFactor.ToTimeSpan(new AVRational { num = timeBase.den, den = timeBase.num }) :
                frame->pkt_duration.ToTimeSpan(component.Stream->time_base);

            // for video frames, we always get the best effort timestamp as dts and pts might
            // contain different times.
            frame->pts = frame->best_effort_timestamp;

            HasValidStartTime = frame->pts != ffmpeg.AV_NOPTS_VALUE;
            StartTime = frame->pts == ffmpeg.AV_NOPTS_VALUE ?
                TimeSpan.FromTicks(0) :
                TimeSpan.FromTicks(frame->pts.ToTimeSpan(StreamTimeBase).Ticks - component.Container.MediaStartTime.Ticks);

            EndTime = TimeSpan.FromTicks(StartTime.Ticks + Duration.Ticks);

            // Picture Number and SMTPE TimeCode
            DisplayPictureNumber = frame->display_picture_number == 0 ?
                Extensions.ComputePictureNumber(StartTime, Duration, 1) :
                frame->display_picture_number;

            CodedPictureNumber = frame->coded_picture_number;
            SmtpeTimeCode = Extensions.ComputeSmtpeTimeCode(component.StartTime, Duration, timeBase, DisplayPictureNumber);
            IsHardwareFrame = component.IsUsingHardwareDecoding;
            HardwareAcceleratorName = component.HardwareAccelerator?.Name;

            // Process side data such as CC packets
            for (var i = 0; i < frame->nb_side_data; i++)
            {
                var sideData = frame->side_data[i];

                // Get the Closed-Caption packets
                if (sideData->type != AVFrameSideDataType.AV_FRAME_DATA_A53_CC)
                    continue;

                // Parse 3 bytes at a time
                for (var p = 0; p < sideData->size; p += 3)
                {
                    var packet = new ClosedCaptionPacket(TimeSpan.FromTicks(StartTime.Ticks + p), sideData->data, p);
                    if (packet.PacketType == CaptionsPacketType.NullPad || packet.PacketType == CaptionsPacketType.Unrecognized)
                        continue;

                    // at this point, we have valid CC data
                    ClosedCaptions.Add(packet);
                }
            }
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets the closed caption data collected from the frame in CEA-708/EAS-608 format.
        /// </summary>
        public List<ClosedCaptionPacket> ClosedCaptions { get; } = new List<ClosedCaptionPacket>(128);

        /// <summary>
        /// Gets the display picture number (frame number).
        /// If not set by the decoder, this attempts to obtain it by dividing the start time by the
        /// frame duration
        /// </summary>
        public long DisplayPictureNumber { get; }

        /// <summary>
        /// Gets the coded picture number set by the decoder.
        /// </summary>
        public long CodedPictureNumber { get; }

        /// <summary>
        /// Gets the SMTPE time code.
        /// </summary>
        public string SmtpeTimeCode { get; }

        /// <summary>
        /// Gets a value indicating whether this frame was decoded in a hardware context.
        /// </summary>
        public bool IsHardwareFrame { get; }

        /// <summary>
        /// Gets the name of the hardware decoder if the frame was decoded in a hardware context.
        /// </summary>
        public string HardwareAcceleratorName { get; }

        /// <summary>
        /// Gets the pointer to the unmanaged frame.
        /// </summary>
        internal AVFrame* Pointer => (AVFrame*)InternalPointer;

        #endregion

        #region Methods

        /// <inheritdoc />
        public override void Dispose()
        {
            lock (DisposeLock)
            {
                if (IsDisposed) return;

                if (InternalPointer != IntPtr.Zero)
                    ReleaseAVFrame(Pointer);

                InternalPointer = IntPtr.Zero;
                IsDisposed = true;
            }
        }

        #endregion
    }
}
