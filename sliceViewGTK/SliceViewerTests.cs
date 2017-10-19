﻿using System;
using System.Collections.Generic;
using System.Linq;
using Gtk;
using GLib;
using g3;
using gs;

namespace SliceViewer
{
	public static class SliceViewerTests
	{


        public static void TestDGraph2()
		{
			Window window = new Window("TestDGraph2");
			window.SetDefaultSize(600, 600);
			window.SetPosition(WindowPosition.Center);

            DebugViewCanvas view = new DebugViewCanvas();

            GeneralPolygon2d poly = new GeneralPolygon2d(
				Polygon2d.MakeCircle(10, 32));

			//Polygon2d hole = Polygon2d.MakeCircle(5, 32);
			//hole.Translate(new Vector2d(2, 0));
			//hole.Reverse();
			//poly.AddHole(hole);

            Polygon2d hole2 = Polygon2d.MakeCircle(1, 32);
            hole2.Translate(-6 * Vector2d.AxisX);
            hole2.Reverse();
            poly.AddHole(hole2);

            Polygon2d hole3 = Polygon2d.MakeCircle(1, 32);
            hole3.Translate(-6 * Vector2d.One);
            hole3.Reverse();
            poly.AddHole(hole3);

            //Polygon2d hole4 = Polygon2d.MakeCircle(1, 32);
            //hole4.Translate(7 * Vector2d.AxisY);
            //hole4.Reverse();
            //poly.AddHole(hole4);

            view.AddPolygon(poly, Colorf.Black);

            double step_size = 0.2;
            double spacing = 0.2;

            //double[] offsets = new double[] { 0.5, 1, 1.5, 2, 2.5 };
            double[] offsets = new double[] { 6 };

            foreach (double offset in offsets) {
                int nsteps = (int)(offset / step_size);
                nsteps += nsteps / 2;
                DGraph2 graph = Offset(poly, offset, nsteps, spacing, false);
                DGraph2Util.Curves c = DGraph2Util.ExtractCurves(graph);
                view.AddGraph(graph, Colorf.Red);
            }


            int[] counts = new int[] { 3, 5, 6, 10, 11 };
            foreach (int NV in counts) {
                Polygon2d p0 = Polygon2d.MakeCircle(10, NV);
                Polygon2dBoxTree tree = new Polygon2dBoxTree(p0);

                Vector2d pt = Vector2d.Zero;
                int poly_nearseg; double poly_neart;
                double poly_dist = p0.DistanceSquared(pt, out poly_nearseg, out poly_neart);
                int tree_nearseg; double tree_neart;
                double tree_dist = tree.SquaredDistance(pt, out tree_nearseg, out tree_neart);
            }


            window.Add(view);
			window.ShowAll();
		}




		static DVector<Vector2d> offset_cache;
        static DVector<Vector2d> collapse_cache;
        static GeneralPolygon2dBoxTree poly_tree;
        static PointGrid2d<int> graph_cache;

        public static DGraph2 Offset(GeneralPolygon2d poly, double fOffset, int nSteps, double fMergeThresh, bool bResolveOverlaps)
        {
			double dt = fOffset / nSteps;

			DGraph2 graph = new DGraph2();
			graph.AppendPolygon(poly.Outer);
			foreach (var h in poly.Holes)
				graph.AppendPolygon(h);

			SplitToMaxEdgeLength(graph, fMergeThresh * 1.5);

			offset_cache = new DVector<Vector2d>();
			offset_cache.resize(graph.VertexCount*2);
            poly_tree = new GeneralPolygon2dBoxTree(poly);
            collapse_cache = new DVector<Vector2d>();
            collapse_cache.resize(graph.VertexCount * 2);

            //graph_cache = new PointGrid2d<int>(poly.Bounds.MaxDim / 64);
            //foreach (int vid in graph.VertexIndices())
            //    graph_cache.InsertPoint(vid, graph.GetVertex(vid));

            LocalProfiler p = new LocalProfiler();

			for (int i = 0; i < nSteps; ++i ) {

				p.Start("offset");

                gParallel.ForEach(graph.VertexIndices(), (vid) => {
                    Vector2d cur_pos = graph.GetVertex(vid);
                    Vector2d new_pos = compute_offset_step(cur_pos, poly, fOffset, dt);
                    Vector2d new_pos_2 = compute_offset_step(new_pos, poly, fOffset, dt);

                    new_pos = Vector2d.Lerp(new_pos, new_pos_2, 0.5);

                    graph.SetVertex(vid, new_pos);
                });

				p.StopAndAccumulate("offset");
				p.Start("smooth");

				SmoothPass(graph, 5, 0.75, fMergeThresh / 2);

				p.StopAndAccumulate("smooth");
				p.Start("join");

				int joined = 0;
				do {
                    //joined = JoinInTolerance(graph, fMergeThresh);
                    joined = JoinInTolerance_Parallel(graph, fMergeThresh);
                } while (joined > 0);

				p.StopAndAccumulate("join");
				p.Start("refine");

				CollapseToMinEdgeLength(graph, fMergeThresh * 0.75f);
				SplitToMaxEdgeLength(graph, fMergeThresh * 1.5);

				p.StopAndAccumulate("refine");
			}

			System.Console.WriteLine(p.AllAccumulatedTimes());

            //SmoothPass(graph, 25, 0.1, fMergeThresh);

            if (bResolveOverlaps) {
                List<int> junctions = new List<int>();
                foreach (int vid in graph.VertexIndices()) {
                    if (graph.GetVtxEdgeCount(vid) > 2)
                        junctions.Add(vid);
                }
                foreach (int vid in junctions) {
                    Vector2d v = graph.GetVertex(vid);
                    int[] nbr_verts = graph.VtxVerticesItr(vid).ToArray();
                    Index2i best_aligned = Index2i.Max; double max_angle = 0;
                    for (int i = 0; i < nbr_verts.Length; ++i) {
                        for (int j = i + 1; j < nbr_verts.Length; ++j) {
                            double angle = Vector2d.AngleD(
                                (graph.GetVertex(nbr_verts[i]) - v).Normalized,
                                (graph.GetVertex(nbr_verts[j]) - v).Normalized);
                            angle = Math.Abs(angle);
                            if (angle > max_angle) {
                                max_angle = angle;
                                best_aligned = new Index2i(nbr_verts[i], nbr_verts[j]);
                            }
                        }
                    }

                    for (int k = 0; k < nbr_verts.Length; ++k) {
                        if (nbr_verts[k] == best_aligned.a || nbr_verts[k] == best_aligned.b)
                            continue;
                        int eid = graph.FindEdge(vid, nbr_verts[k]);
                        graph.RemoveEdge(eid, true);
                        if (graph.IsVertex(nbr_verts[k])) {
                            Vector2d newpos = Vector2d.Lerp(graph.GetVertex(nbr_verts[k]), v, 0.99);
                            int newv = graph.AppendVertex(newpos);
                            graph.AppendEdge(nbr_verts[k], newv);
                        }
                    }
                }

                PathOverlapRepair repair = new PathOverlapRepair(graph);
                repair.Compute();
            }


            return graph;
		}





		public static Vector2d compute_offset_step(Vector2d cur_pos, GeneralPolygon2d poly, double fTargetOffset, double stepSize) {

			int iHole, iSeg; double segT;
            double distSqr =
                //poly.DistanceSquared(cur_pos, out iHole, out iSeg, out segT);
                poly_tree.DistanceSquared(cur_pos, out iHole, out iSeg, out segT);

            double dist = Math.Sqrt(distSqr);
			Vector2d normal = poly.GetNormal(iSeg, segT, iHole);

			double step = stepSize;
			if (dist > fTargetOffset) {
				step = Math.Max(fTargetOffset - dist, -step);
			} else {
				step = Math.Min(fTargetOffset - dist, step);
			}

			Vector2d new_pos = cur_pos - step * normal;
			return new_pos;
		}







		public static void SmoothPass(DGraph2 graph, int passes, double smooth_alpha, double max_move)
		{
			double max_move_sqr = max_move * max_move;
			int NV = graph.MaxVertexID;
			DVector<Vector2d> smoothedV = offset_cache;
			if (smoothedV.size < NV)
				smoothedV.resize(NV);

			for (int pi = 0; pi < passes; ++pi) {

                gParallel.ForEach(Interval1i.Range(NV), (vid) => {
                    if (!graph.IsVertex(vid))
                        return;
                    Vector2d v = graph.GetVertex(vid);
                    Vector2d c = Vector2d.Zero;
                    int n = 0;
                    foreach (int vnbr in graph.VtxVerticesItr(vid)) {
                        c += graph.GetVertex(vnbr);
                        n++;
                    }
                    if (n >= 2) {
                        c /= n;
                        Vector2d dv = (smooth_alpha) * (c - v);
                        if (dv.LengthSquared > max_move_sqr) {
                            double d = dv.Normalize();
                            dv *= max_move;
                        }
                        v += dv;
                    }
                    smoothedV[vid] = v;
                });

				for (int vid = 0; vid < NV; ++vid) {
					if (graph.IsVertex(vid))
						graph.SetVertex(vid, smoothedV[vid]);
				}
			}

		}



		public static int JoinInTolerance(DGraph2 graph, double fMergeDist) {
			double mergeSqr = fMergeDist * fMergeDist;

			int merged = 0;
			int NV = graph.MaxVertexID;
			for (int a = 0; a < NV; ++a) {
				if (!graph.IsVertex(a))
					continue;
				Vector2d va = graph.GetVertex(a);

				int bNearest = -1;
				double nearDistSqr = double.MaxValue;
                // THIS IS WRONG we can't only search ahead!
                // ACTUALLY we can but we will miss some collapses (.)
                for (int b = a + 1; b < NV; ++b) {
                //for (int b = 0; b < NV; ++b) {
                    if ( b == a || graph.IsVertex(b) == false)
						continue;
					double distsqr = va.DistanceSquared(graph.GetVertex(b));
					if (distsqr < mergeSqr && distsqr < nearDistSqr) {
						if (graph.FindEdge(a, b) == DGraph2.InvalidID) {
							nearDistSqr = distsqr;
							bNearest = b;
						}
					}
				}
				if (bNearest == -1)
					continue;

				int eid = graph.AppendEdge(a, bNearest);
				DGraph2.EdgeCollapseInfo collapseInfo;
				graph.CollapseEdge(bNearest, a, out collapseInfo);
				merged++;
			}
			return merged;
		}




        public static int JoinInTolerance_Parallel(DGraph2 graph, double fMergeDist)
        {
            double mergeSqr = fMergeDist * fMergeDist;

            int NV = graph.MaxVertexID;
            if ( collapse_cache.size < NV )
                collapse_cache.resize(NV);

            gParallel.ForEach( Interval1i.Range(NV), (a) => {
                collapse_cache[a] = new Vector2d(-1, double.MaxValue);
                if (!graph.IsVertex(a))
                    return;

                Vector2d va = graph.GetVertex(a);

                int bNearest = -1;
                double nearDistSqr = double.MaxValue;
                for (int b = a + 1; b < NV; ++b) {
                    if (b == a || graph.IsVertex(b) == false)
                        continue;
                    double distsqr = va.DistanceSquared(graph.GetVertex(b));
                    if (distsqr < mergeSqr && distsqr < nearDistSqr) {
                        if (graph.FindEdge(a, b) == DGraph2.InvalidID) {
                            nearDistSqr = distsqr;
                            bNearest = b;
                        }
                    }
                }
                if (bNearest != -1)
                    collapse_cache[a] = new Vector2d(bNearest, nearDistSqr);
            });

            // [TODO] sort

            int merged = 0;
            for (int a = 0; a < NV; ++a) {
                if (collapse_cache[a].x == -1)
                    continue;

                int bNearest = (int)collapse_cache[a].x;

                int eid = graph.AppendEdge(a, bNearest);
                DGraph2.EdgeCollapseInfo collapseInfo;
                graph.CollapseEdge(bNearest, a, out collapseInfo);
                merged++;
            }
            return merged;
        }





        public static void CollapseToMinEdgeLength(DGraph2 graph, double fMinLen) {
            double sharp_threshold_deg = 140.0f;

			double minLenSqr = fMinLen * fMinLen;
			bool done = false;
            int max_passes = 100;
            int pass_count = 0;
			while (done == false && pass_count++ < max_passes) {
				done = true;

                // [RMS] do modulo-indexing here to avoid pathological cases where we do things like
                // continually collapse a short edge adjacent to a long edge (which will result in crazy over-collapse)
                int N = graph.MaxEdgeID;
                const int nPrime = 31337;     // any prime will do...
                int cur_eid = 0;
                do {
                    int eid = cur_eid;
                    cur_eid = (cur_eid + nPrime) % N;

                    if (!graph.IsEdge(eid))
                        continue;
                    Index2i ev = graph.GetEdgeV(eid);

                    Vector2d va = graph.GetVertex(ev.a);
                    Vector2d vb = graph.GetVertex(ev.b);
                    double distSqr = va.DistanceSquared(vb);
                    if (distSqr < minLenSqr) {

                        int vtx_idx = -1;    // collapse to this vertex

                        // check valences. want to preserve positions of non-valence-2
                        int na = graph.GetVtxEdgeCount(ev.a);
                        int nb = graph.GetVtxEdgeCount(ev.b);
                        if (na != 2 && nb != 2)
                            continue;
                        if (na != 2)
                            vtx_idx = 0;
                        else if (nb != 2)
                            vtx_idx = 1;

                        // check opening angles. want to preserve sharp(er) angles
                        if (vtx_idx == -1) {
                            double opena = Math.Abs(graph.OpeningAngle(ev.a));
                            double openb = Math.Abs(graph.OpeningAngle(ev.b));
                            if (opena < sharp_threshold_deg && openb < sharp_threshold_deg)
                                continue;
                            else if (opena < sharp_threshold_deg)
                                vtx_idx = 0;
                            else if (openb < sharp_threshold_deg)
                                vtx_idx = 1;
                        }

                        Vector2d newPos = (vtx_idx == -1) ? 0.5 * (va + vb) : ((vtx_idx == 0) ? va : vb);

                        int keep = ev.a, remove = ev.b;
                        if (vtx_idx == 1) {
                            remove = ev.a; keep = ev.b;
                        }

                        DGraph2.EdgeCollapseInfo collapseInfo;
                        if (graph.CollapseEdge(keep, remove, out collapseInfo) == MeshResult.Ok) {
                            graph.SetVertex(collapseInfo.vKept, newPos);
                            done = false;
                        }
                    }

                } while (cur_eid != 0);
			}
		}












        public static void CollapseFlatVertices(DGraph2 graph, double fMaxDeviationDeg = 5)
        {
            bool done = false;
            int max_passes = 200;
            int pass_count = 0;
            while (done == false && pass_count++ < max_passes) {
                done = true;

                // [RMS] do modulo-indexing here to avoid pathological cases where we do things like
                // continually collapse a short edge adjacent to a long edge (which will result in crazy over-collapse)
                int N = graph.MaxVertexID;
                const int nPrime = 31337;     // any prime will do...
                int cur_vid = 0;
                do {
                    int vid = cur_vid;
                    cur_vid = (cur_vid + nPrime) % N;

                    if (!graph.IsVertex(vid))
                        continue;
                    if (graph.GetVtxEdgeCount(vid) != 2)
                        continue;

                    double open = Math.Abs(graph.OpeningAngle(vid));
                    if (open < 180 - fMaxDeviationDeg)
                        continue;

                    int eid = graph.GetVtxEdges(vid).First();

                    Index2i ev = graph.GetEdgeV(eid);
                    int other_v = (ev.a == vid) ? ev.b : ev.a;

                    DGraph2.EdgeCollapseInfo collapseInfo;
                    MeshResult result = graph.CollapseEdge(other_v, vid, out collapseInfo);
                    if ( result == MeshResult.Ok) {
                        done = false;
                    } else {
                        System.Console.WriteLine("wha?");
                    }

                } while (cur_vid != 0);
            }
        }












        public static void SplitToMaxEdgeLength(DGraph2 graph, double fMaxLen) {
			List<int> queue = new List<int>();
			int NE = graph.MaxEdgeID;
			for (int eid = 0; eid < NE; ++eid) {
				if (!graph.IsEdge(eid))
					continue;
				Index2i ev = graph.GetEdgeV(eid);
				double dist = graph.GetVertex(ev.a).Distance(graph.GetVertex(ev.b));
				if (dist > fMaxLen) {
					DGraph2.EdgeSplitInfo splitInfo;
					if (graph.SplitEdge(eid, out splitInfo) == MeshResult.Ok && dist > 2 * fMaxLen) {
						queue.Add(eid);
						queue.Add(splitInfo.eNewBN);
					}
				}
			}
			while (queue.Count > 0) {
				int eid = queue[queue.Count - 1];
				queue.RemoveAt(queue.Count - 1);
				if (!graph.IsEdge(eid))
					continue;
				Index2i ev = graph.GetEdgeV(eid);
				double dist = graph.GetVertex(ev.a).Distance(graph.GetVertex(ev.b));
				if (dist > fMaxLen) {
					DGraph2.EdgeSplitInfo splitInfo;
					if (graph.SplitEdge(eid, out splitInfo) == MeshResult.Ok && dist > 2 * fMaxLen) {
						queue.Add(eid);
						queue.Add(splitInfo.eNewBN);
					}
				}				
			}
		}






        public class PointGrid2d<T>
        {
            Dictionary<Vector2i, List<T>> Hash;
            ScaleGridIndexer2 Indexer;

            public PointGrid2d(double cellSize)
            {
                Hash = new Dictionary<Vector2i, List<T>>();
                Indexer = new ScaleGridIndexer2() { CellSize = cellSize };
            }


            public void InsertPoint(T value, Vector2d pos)
            {
                Vector2i idx = Indexer.ToGrid(pos);
                insert_point(value, idx);
            }
            void insert_point(T value, Vector2i idx)
            {
                List<T> values;
                if (Hash.TryGetValue(idx, out values)) {
                    values.Add(value);
                } else {
                    Hash[idx] = new List<T>() { value };
                }
            }


            public bool RemovePoint(T value, Vector2d pos)
            {
                Vector2i idx = Indexer.ToGrid(pos);
                return remove_point(value, idx);
            }
            bool remove_point(T value, Vector2i idx)
            {
                List<T> values;
                if (Hash.TryGetValue(idx, out values)) {
                    return values.Remove(value);
                } else
                    return false;            }


            public void UpdatePoint(T value, Vector2d old_pos, Vector2d new_pos)
            {
                Vector2i old_idx = Indexer.ToGrid(old_pos);
                Vector2i new_idx = Indexer.ToGrid(new_pos);
                if (old_idx == new_idx)
                    return;
                bool ok = remove_point(value, old_idx);
                if (!ok)
                    throw new Exception("PointGrid2d.UpdatePoint: point " + value.ToString() + " was not in grid!");
                insert_point(value, new_idx);
                return;
            }


            public KeyValuePair<T,double> FindNearestInRadius(Vector2d query_pt, double radius, Func<T, double> distF, Func<T, bool> ignoreF )
            {
                Vector2i min_idx = Indexer.ToGrid(query_pt - radius * Vector2d.One);
                Vector2i max_idx = Indexer.ToGrid(query_pt + radius * Vector2d.One);

                double min_dist = double.MaxValue;
                T nearest = default(T);

                for ( int yi = min_idx.y; yi <= max_idx.y; yi++ ) {
                    for ( int xi = min_idx.x; xi <= max_idx.x; xi++ ) {
                        Vector2i idx = new Vector2i(xi, yi);
                        List<T> values;
                        if (Hash.TryGetValue(idx, out values) == false)
                            continue;
                        foreach ( T value in values ) {
                            if ( ignoreF(value) )
                                continue;
                            double dist = distF(value);
                            if ( dist < radius && dist < min_dist ) {
                                nearest = value;
                                min_dist = dist;
                            }
                        }
                    }
                }

                return new KeyValuePair<T, double>(nearest, min_dist);
            }


        }




 



	}
}
