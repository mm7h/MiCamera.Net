#!/usr/bin/env python3
"""Create the four Jenkins SCM jobs. Does not publish source, install plugins or run builds."""
import argparse
import base64
import http.cookiejar
import json
import os
from pathlib import Path
import urllib.error
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET


BASE = "http://192.168.3.102"
# This Jenkins folder is displayed as AI Agent Practice but its item name is XiaoZhi.
PARENT = "/job/XiaoZhi"
FOLDER = PARENT + "/job/MiCamera.Net"
JOBS = {"Frontend": "web", "Backend": "bridge", "Miloco": "miloco", "Reset": "reset"}
REQUIRED_PLUGINS = {"cloudbees-folder", "workflow-job", "workflow-cps", "workflow-durable-task-step",
                    "workflow-basic-steps", "workflow-scm-step", "pipeline-model-definition", "git",
                    "credentials-binding", "ssh-credentials", "timestamper"}


def job_xml(service):
    root = ET.Element("flow-definition")
    ET.SubElement(root, "description").text = ("Reset all application credentials/configuration on 104; manual confirmation required."
                                               if service == "reset" else f"Build develop and deploy only the {service} service to 104.")
    ET.SubElement(root, "keepDependencies").text = "false"
    properties = ET.SubElement(root, "properties")
    if service == "reset":
        parameters = ET.SubElement(ET.SubElement(properties, "hudson.model.ParametersDefinitionProperty"), "parameterDefinitions")
        parameter = ET.SubElement(parameters, "hudson.model.StringParameterDefinition")
        ET.SubElement(parameter, "name").text = "CONFIRM_RESET"
        ET.SubElement(parameter, "description").text = "Type MiCamera.Net to reset all application credentials and configuration."
        ET.SubElement(parameter, "defaultValue").text = ""
        ET.SubElement(parameter, "trim").text = "false"
    definition = ET.SubElement(root, "definition", {"class": "org.jenkinsci.plugins.workflow.cps.CpsScmFlowDefinition"})
    scm = ET.SubElement(definition, "scm", {"class": "hudson.plugins.git.GitSCM"})
    ET.SubElement(scm, "configVersion").text = "2"
    remote = ET.SubElement(ET.SubElement(scm, "userRemoteConfigs"), "hudson.plugins.git.UserRemoteConfig")
    ET.SubElement(remote, "url").text = "git@github.com:mm7h/MiCamera.Net.git"
    ET.SubElement(remote, "credentialsId").text = "micamera-github-readonly"
    branch = ET.SubElement(ET.SubElement(scm, "branches"), "hudson.plugins.git.BranchSpec")
    ET.SubElement(branch, "name").text = "*/develop"
    ET.SubElement(scm, "doGenerateSubmoduleConfigurations").text = "false"
    ET.SubElement(scm, "submoduleCfg", {"class": "empty-list"})
    ET.SubElement(scm, "extensions")
    ET.SubElement(definition, "scriptPath").text = f"deployment/jenkins/Jenkinsfile.{service}"
    ET.SubElement(definition, "lightweight").text = "false"
    ET.SubElement(root, "triggers")
    ET.SubElement(root, "disabled").text = "false"
    return ET.tostring(root, encoding="utf-8", xml_declaration=True)


class Jenkins:
    def __init__(self):
        user = os.environ["JENKINS_USER"]
        credential = os.environ.get("JENKINS_API_TOKEN") or os.environ["JENKINS_PASSWORD"]
        self.authorization = "Basic " + base64.b64encode((user + ":" + credential).encode()).decode()
        self.opener = urllib.request.build_opener(urllib.request.ProxyHandler({}),
                                                 urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
        self.crumb = None

    def request(self, path, data=None, content_type="application/xml"):
        headers = {"Authorization": self.authorization, "Content-Type": content_type}
        if data is not None:
            if self.crumb is None:
                status, content = self.request("/crumbIssuer/api/json")
                crumb = json.loads(content) if status == 200 else {}
                self.crumb = {crumb["crumbRequestField"]: crumb["crumb"]} if crumb else {}
            headers.update(self.crumb)
        request = urllib.request.Request(BASE + path, data=data, headers=headers)
        try:
            with self.opener.open(request, timeout=30) as response:
                return response.status, response.read()
        except urllib.error.HTTPError as error:
            if error.code == 404:
                return 404, b""
            raise RuntimeError(f"Jenkins returned HTTP {error.code}; response content is hidden.") from None

    def get(self, path):
        status, data = self.request(path)
        if status != 200:
            raise RuntimeError("Required Jenkins resource was not found.")
        return json.loads(data)

    def create(self, parent, name, xml):
        path = parent + "/job/" + urllib.parse.quote(name, safe="")
        if self.request(path + "/api/json")[0] == 200:
            raise RuntimeError(f"Existing item {name} will not be overwritten; review its configuration separately.")
        self.request(parent + "/createItem?name=" + urllib.parse.quote(name, safe=""), xml)
        print("Created Jenkins item:", name)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--apply", action="store_true", help="Create missing folders/jobs after reviewing the generated XML.")
    parser.add_argument("--export", type=Path, help="Export reviewable XML without contacting Jenkins.")
    arguments = parser.parse_args()
    if arguments.export:
        arguments.export.mkdir(parents=True, exist_ok=True)
        for job, service in JOBS.items():
            (arguments.export / (job + ".xml")).write_bytes(job_xml(service))
        return
    jenkins = Jenkins()
    plugins = jenkins.get("/pluginManager/api/json?tree=plugins[shortName,active]")["plugins"]
    missing = REQUIRED_PLUGINS - {plugin["shortName"] for plugin in plugins if plugin["active"]}
    if missing:
        raise RuntimeError("Required plugins are missing: " + ", ".join(sorted(missing)))
    parent = jenkins.get(PARENT + "/api/json?tree=_class")
    if parent["_class"] != "com.cloudbees.hudson.plugins.folder.Folder":
        raise RuntimeError("AI Agent Practice must be a Jenkins Folder.")
    nodes = jenkins.get("/computer/api/json?tree=computer[displayName,offline,assignedLabels[name]]")["computer"]
    if not any(not node["offline"] and any(label["name"] == "micamera-build" for label in node["assignedLabels"]) for node in nodes):
        raise RuntimeError("An online micamera-build node on 102 is required.")
    if not arguments.apply:
        print("Jenkins plugins and build-node label verified. Use --apply after publishing the source and configuring credentials.")
        return
    if jenkins.request(FOLDER + "/api/json")[0] == 404:
        jenkins.create(PARENT, "MiCamera.Net", b'<com.cloudbees.hudson.plugins.folder.Folder><description>MiCamera.Net deployments to 104</description><properties/></com.cloudbees.hudson.plugins.folder.Folder>')
    for job, service in JOBS.items():
        if jenkins.request(FOLDER + "/job/" + job + "/api/json")[0] == 404:
            jenkins.create(FOLDER, job, job_xml(service))
        else:
            print("Existing job retained:", job)


if __name__ == "__main__":
    try:
        main()
    except (RuntimeError, KeyError, urllib.error.URLError) as error:
        print(str(error) if isinstance(error, RuntimeError) else "Jenkins access/configuration is incomplete.")
        raise SystemExit(1)
