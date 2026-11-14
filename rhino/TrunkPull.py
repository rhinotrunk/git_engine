#! python3
# Rhino alias:  TrunkPull   ! _-RunPythonScript "C:\Users\rober\repos\git_engine\rhino\TrunkPull.py"
import os
RT_CMD = "pull"
RT_HOME = os.path.dirname(os.path.abspath(__file__)) if "__file__" in globals() else r"C:\Users\rober\repos\git_engine\rhino"
exec(open(os.path.join(RT_HOME, "rhinotrunk.py"), encoding="utf-8").read(), globals())
