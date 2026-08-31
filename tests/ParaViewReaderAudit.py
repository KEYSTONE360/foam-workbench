"""Open a generated FOAM Workbench case with ParaView's real OpenFOAM reader."""

import json
import os
import sys

from paraview import servermanager
from paraview.simple import OpenFOAMReader, UpdatePipeline


def array_names(attributes):
    return {
        attributes.GetArrayName(index)
        for index in range(attributes.GetNumberOfArrays())
        if attributes.GetArrayName(index)
    }


def collect_arrays(data_object):
    cell_arrays = set()
    point_arrays = set()
    if data_object.IsA("vtkCompositeDataSet"):
        iterator = data_object.NewIterator()
        iterator.SkipEmptyNodesOn()
        iterator.InitTraversal()
        while not iterator.IsDoneWithTraversal():
            block = iterator.GetCurrentDataObject()
            if block:
                cell_arrays.update(array_names(block.GetCellData()))
                point_arrays.update(array_names(block.GetPointData()))
            iterator.GoToNextItem()
    else:
        cell_arrays.update(array_names(data_object.GetCellData()))
        point_arrays.update(array_names(data_object.GetPointData()))
    return sorted(cell_arrays), sorted(point_arrays)


if len(sys.argv) != 2:
    raise SystemExit("usage: pvpython ParaViewReaderAudit.py <case.foam>")

case_marker = os.path.abspath(sys.argv[1])
reader = OpenFOAMReader(registrationName="FOAM Workbench audit", FileName=case_marker)

available_fields = list(reader.CellArrays.Available)
reader.CellArrays = available_fields
available_regions = list(reader.MeshRegions.Available)
if "internalMesh" in available_regions:
    reader.MeshRegions = ["internalMesh"]

times = list(reader.TimestepValues)
latest_time = max(times) if times else 0.0
UpdatePipeline(time=latest_time, proxy=reader)
fetched = servermanager.Fetch(reader)
cell_arrays, point_arrays = collect_arrays(fetched)
visible_arrays = set(cell_arrays) | set(point_arrays)
required = {"U", "p", "layerId", "permeability"}

result = {
    "case": case_marker,
    "reader": "ParaView OpenFOAMReader",
    "availableFields": available_fields,
    "availableRegions": available_regions,
    "times": times,
    "latestTime": latest_time,
    "cellArrays": cell_arrays,
    "pointArrays": point_arrays,
    "requiredArrays": sorted(required),
    "missingArrays": sorted(required - visible_arrays),
    "pass": required.issubset(visible_arrays) and latest_time > 0,
}
print(json.dumps(result, indent=2))
raise SystemExit(0 if result["pass"] else 1)
