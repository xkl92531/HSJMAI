import xml.etree.ElementTree as ET
xmlpath = r"d:\hsjmwork\HSJM_AI_PLC\PLC\u6587\u4ef6\FB3002_Valve.xml"
tree = ET.parse(xmlpath)
root = tree.getroot()
cus = root.findall(".//{http://www.siemens.com/automation/Openness/SW/Blocks/v3}SW.Blocks.CompileUnit")
print("CU count:", len(cus))
for i, cu in enumerate(cus):
    cu_id = cu.get("ID", str(i))
    for access in cu.iter():
        if not access.tag.endswith("Access"): continue
        comps = [c.get("Name","") for c in access if c.tag.endswith("Component")]
        if len(comps)==1 and comps[0] in ("IHP","BkdAuto","FwdPos","BkdPos"):
            print("  CU=%s UId=%s Scope=%s Name=%s" % (cu_id, access.get("UId"), access.get("Scope"), comps[0]))
