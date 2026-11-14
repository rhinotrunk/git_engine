#! python3
# Rhino alias:  TrunkStatus   ! _-RunPythonScript "C:\Users\rober\repos\git_engine\rhino\TrunkStatus.py"
import os
RT_CMD = "status"
RT_HOME = os.path.dirname(os.path.abspath(__file__)) if "__file__" in globals() else r"C:\Users\rober\repos\git_engine\rhino"
exec(open(os.path.join(RT_HOME, "rhinotrunk.py"), encoding="utf-8").read(), globals())
