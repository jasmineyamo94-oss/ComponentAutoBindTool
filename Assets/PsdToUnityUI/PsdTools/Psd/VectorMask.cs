using System;
using System.Collections.Generic;
using PsdTools.Utils;

namespace PsdTools.Psd
{
    /// <summary>
    /// Path point type
    /// </summary>
    public enum PathPointType : ushort
    {
        ClosedSubpathLength = 0,
        ClosedSubpathBezierKnotLinked = 1,
        ClosedSubpathBezierKnotUnlinked = 2,
        OpenSubpathLength = 3,
        OpenSubpathBezierKnotLinked = 4,
        OpenSubpathBezierKnotUnlinked = 5,
        PathFillRule = 6,
        Clipboard = 7,
        InitialFillRule = 8
    }

    /// <summary>
    /// Bezier knot point
    /// </summary>
    public struct BezierKnot
    {
        /// <summary>Preceding control point (in)</summary>
        public (double X, double Y) In;

        /// <summary>Anchor point</summary>
        public (double X, double Y) Anchor;

        /// <summary>Following control point (out)</summary>
        public (double X, double Y) Out;

        /// <summary>Whether control points are linked</summary>
        public bool Linked;
    }

    /// <summary>
    /// Path (subpath) in a vector mask
    /// </summary>
    public class VectorPath
    {
        /// <summary>Whether path is closed</summary>
        public bool Closed { get; set; }

        /// <summary>Path knots</summary>
        public List<BezierKnot> Knots { get; set; }

        public VectorPath()
        {
            Knots = new List<BezierKnot>();
        }
    }

    /// <summary>
    /// Vector mask data
    /// </summary>
    public class VectorMask
    {
        /// <summary>Mask paths</summary>
        public List<VectorPath> Paths { get; private set; }

        /// <summary>Whether mask is inverted</summary>
        public bool Inverted { get; set; }

        /// <summary>Whether mask is disabled</summary>
        public bool Disabled { get; set; }

        /// <summary>Initial fill rule (0 = all pixels inside path are masked)</summary>
        public int InitialFill { get; set; }

        public VectorMask()
        {
            Paths = new List<VectorPath>();
        }

        /// <summary>
        /// Parse vector mask from tagged block data
        /// </summary>
        public static VectorMask Parse(byte[] data)
        {
            if (data == null || data.Length < 10)
                return null;

            var mask = new VectorMask();

            try
            {
                using (var reader = new BigEndianReader(data))
                {
                    // Version (4 bytes)
                    uint version = reader.ReadUInt32();

                    // Flags (4 bytes)
                    uint flags = reader.ReadUInt32();
                    mask.Inverted = (flags & 1) != 0;
                    mask.Disabled = (flags & 2) != 0;

                    // Parse path records
                    VectorPath currentPath = null;

                    while (reader.Remaining >= 26) // Minimum path record size
                    {
                        ushort selector = reader.ReadUInt16();
                        var type = (PathPointType)selector;

                        switch (type)
                        {
                            case PathPointType.ClosedSubpathLength:
                            case PathPointType.OpenSubpathLength:
                                // New subpath
                                currentPath = new VectorPath
                                {
                                    Closed = type == PathPointType.ClosedSubpathLength
                                };
                                mask.Paths.Add(currentPath);
                                // Length record contains knot count in next bytes
                                reader.Skip(24); // Rest of record
                                break;

                            case PathPointType.ClosedSubpathBezierKnotLinked:
                            case PathPointType.ClosedSubpathBezierKnotUnlinked:
                            case PathPointType.OpenSubpathBezierKnotLinked:
                            case PathPointType.OpenSubpathBezierKnotUnlinked:
                                if (currentPath != null)
                                {
                                    var knot = ReadBezierKnot(reader);
                                    knot.Linked = type == PathPointType.ClosedSubpathBezierKnotLinked ||
                                                 type == PathPointType.OpenSubpathBezierKnotLinked;
                                    currentPath.Knots.Add(knot);
                                }
                                else
                                {
                                    reader.Skip(24);
                                }
                                break;

                            case PathPointType.PathFillRule:
                                reader.Skip(24);
                                break;

                            case PathPointType.InitialFillRule:
                                // Next 2 bytes contain initial fill value
                                mask.InitialFill = reader.ReadInt16();
                                reader.Skip(22);
                                break;

                            case PathPointType.Clipboard:
                                reader.Skip(24);
                                break;

                            default:
                                reader.Skip(24);
                                break;
                        }
                    }
                }
            }
            catch
            {
                // Parsing failed
            }

            return mask;
        }

        private static BezierKnot ReadBezierKnot(BigEndianReader reader)
        {
            // Each point is stored as fixed-point 8.24 format
            // 6 values: in.y, in.x, anchor.y, anchor.x, out.y, out.x
            double inY = ReadFixedPoint824(reader);
            double inX = ReadFixedPoint824(reader);
            double anchorY = ReadFixedPoint824(reader);
            double anchorX = ReadFixedPoint824(reader);
            double outY = ReadFixedPoint824(reader);
            double outX = ReadFixedPoint824(reader);

            return new BezierKnot
            {
                In = (inX, inY),
                Anchor = (anchorX, anchorY),
                Out = (outX, outY)
            };
        }

        private static double ReadFixedPoint824(BigEndianReader reader)
        {
            // 8.24 fixed point (4 bytes)
            int value = reader.ReadInt32();
            return value / 16777216.0; // 2^24
        }
    }
}
