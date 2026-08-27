namespace Mediapipe.Unity
{
    /// <summary>
    /// Fresh-frame contract used by camera sources whose frame cadence is not
    /// represented by WebCamTexture.didUpdateThisFrame.
    ///
    /// This is a source-acquisition contract only. It does not own tracking,
    /// prediction, filtering, provider selection, or presentation authority.
    /// </summary>
    public interface IKiwiFreshFrameSource
    {
        /// <summary>
        /// Requests that the newest published camera frame be made visible to
        /// Unity's graphics queue before later GPU work in this Unity frame.
        /// Implementations must keep latest-frame semantics and must not build
        /// an unbounded frame queue.
        /// </summary>
        void PrepareFrameForUnity();

        /// <summary>
        /// Returns the exact source sequence/timestamp selected by
        /// PrepareFrameForUnity.
        /// </summary>
        bool TryGetLatestPresentedFrame(
            out ulong sequence,
            out long hostTicks);
    }
}
