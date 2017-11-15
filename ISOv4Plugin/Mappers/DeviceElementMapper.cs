﻿/*
 * ISO standards can be purchased through the ANSI webstore at https://webstore.ansi.org
*/

using AgGateway.ADAPT.ApplicationDataModel.Equipment;
using AgGateway.ADAPT.ApplicationDataModel.Representations;
using AgGateway.ADAPT.ISOv4Plugin.ExtensionMethods;
using AgGateway.ADAPT.ISOv4Plugin.ISOEnumerations;
using AgGateway.ADAPT.ISOv4Plugin.ISOModels;
using AgGateway.ADAPT.ISOv4Plugin.ObjectModel;
using AgGateway.ADAPT.Representation.RepresentationSystem;
using AgGateway.ADAPT.Representation.RepresentationSystem.ExtensionMethods;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AgGateway.ADAPT.ISOv4Plugin.Mappers
{
    public interface IDeviceElementMapper
    {
        IEnumerable<ISODeviceElement> ExportDeviceElements(IEnumerable<DeviceElement> adaptDeviceElements, ISODevice isoDevice);
        ISODeviceElement ExportDeviceElement(DeviceElement adaptDeviceElement, ISODevice isoDevice, List<ISODeviceElement> pendingDeviceElements);
        IEnumerable<DeviceElement> ImportDeviceElements(ISODevice isoDevice);
        DeviceElement ImportDeviceElement(ISODeviceElement isoDeviceElement, EnumeratedValue deviceClassification, DeviceElementHierarchy rootDeviceHierarchy);
    }

    public class DeviceElementMapper : BaseMapper, IDeviceElementMapper
    {
        public DeviceElementMapper(TaskDataMapper taskDataMapper) : base(taskDataMapper, "DET")
        {
        }

        #region Export
        public IEnumerable<ISODeviceElement> ExportDeviceElements(IEnumerable<DeviceElement> adaptDeviceElements, ISODevice isoDevice)
        {
            List<ISODeviceElement> isoDeviceElements = new List<ISODeviceElement>();
            foreach (DeviceElement adaptDeviceElement in adaptDeviceElements)
            {
                ISODeviceElement isoGroup = ExportDeviceElement(adaptDeviceElement, isoDevice, isoDeviceElements);
                isoDeviceElements.Add(isoGroup);
            }
            return isoDeviceElements;
        }

        public ISODeviceElement ExportDeviceElement(DeviceElement adaptDeviceElement, ISODevice isoDevice, List<ISODeviceElement> pendingDeviceElements)
        {
            ISODeviceElement det = new ISODeviceElement(isoDevice);

            //ID
            string id = adaptDeviceElement.Id.FindIsoId() ?? GenerateId();
            det.DeviceElementId = id;
            ExportUniqueIDs(adaptDeviceElement.Id, id);
            TaskDataMapper.ISOIdMap.Add(adaptDeviceElement.Id.ReferenceId, id);

            //Designator
            det.DeviceElementDesignator = adaptDeviceElement.Description;

            //Device Element Type
            switch (adaptDeviceElement.DeviceElementType)
            {
                case DeviceElementTypeEnum.Machine:
                case DeviceElementTypeEnum.Implement:
                    det.DeviceElementType = ISODeviceElementType.Device;
                    break;
                case DeviceElementTypeEnum.Bin:
                    det.DeviceElementType = ISODeviceElementType.Bin;
                    break;
                case DeviceElementTypeEnum.Function:
                    det.DeviceElementType = ISODeviceElementType.Function;
                    break;
                case DeviceElementTypeEnum.Section:
                    det.DeviceElementType = ISODeviceElementType.Section;
                    break;
                case DeviceElementTypeEnum.Unit:
                    det.DeviceElementType = ISODeviceElementType.Unit;
                    break;
            }

            //Parent ID
            DeviceElement parentDeviceElement = DataModel.Catalog.DeviceElements.FirstOrDefault(d => d.Id.ReferenceId == adaptDeviceElement.ParentDeviceId);
            if (parentDeviceElement != null)
            {
                string deviceElementID = TaskDataMapper.ISOIdMap[parentDeviceElement.Id.ReferenceId];
                if (pendingDeviceElements.Any(d => d.DeviceElementId == deviceElementID))
                {
                    det.ParentObjectId = pendingDeviceElements.First(d => d.DeviceElementId == deviceElementID).DeviceElementObjectId;
                }
            }
            else
            {
                DeviceModel parentDeviceModel = DataModel.Catalog.DeviceModels.FirstOrDefault(d => d.Id.ReferenceId == adaptDeviceElement.ParentDeviceId);
                if (parentDeviceModel != null)
                {
                    //Parent is Device
                    det.ParentObjectId = 0;
                }
            }

            return det;
        }

        #endregion Export 

        #region Import

        public IEnumerable<DeviceElement> ImportDeviceElements(ISODevice isoDevice)
        {
            EnumeratedValue deviceClassification = DecodeMachineInfo(isoDevice.ClientNAME);

            ISODeviceElement rootDeviceElement = isoDevice.DeviceElements.SingleOrDefault(det => det.DeviceElementType == ISODeviceElementType.Device);
            if (rootDeviceElement == null)
            {
                //Short circuit on invalid TaskData
                return null;
            }

            DeviceElementHierarchy rootDeviceElementHierarchy = TaskDataMapper.DeviceElementHierarchies.Items[isoDevice.DeviceId];

            //Import device elements
            List<DeviceElement> adaptDeviceElements = new List<DeviceElement>();

            //Import down the hierarchy to ensure integrity of parent references
            for (int i = 0; i <= rootDeviceElementHierarchy.GetMaxDepth(); i++)
            {
                IEnumerable<ISODeviceElement> isoDeviceElements = rootDeviceElementHierarchy.GetElementsAtDepth(i).Select(h => h.DeviceElement);
                foreach (ISODeviceElement isoDeviceElement in isoDeviceElements)
                {
                    if (isoDeviceElement.DeviceElementType != ISODeviceElementType.Connector)
                    {
                        DeviceElement adaptDeviceElement = ImportDeviceElement(isoDeviceElement, deviceClassification, rootDeviceElementHierarchy);
                        if (isoDeviceElement.DeviceElementType == ISODeviceElementType.Device)
                        {
                            //Setting the Device serial number on the root Device Element only
                            adaptDeviceElement.SerialNumber = isoDevice.DeviceSerialNumber;
                        }
                        adaptDeviceElements.Add(adaptDeviceElement);
                        DataModel.Catalog.DeviceElements.Add(adaptDeviceElement);
                    }
                    else
                    {
                        //Connectors are not represented as DeviceElements in ADAPT
                        AddConnector(rootDeviceElementHierarchy, isoDeviceElement);
                    }
                }
            }

            return adaptDeviceElements;
        }

        public DeviceElement ImportDeviceElement(ISODeviceElement isoDeviceElement, EnumeratedValue deviceClassification, DeviceElementHierarchy rootDeviceHierarchy)
        {
            DeviceElement deviceElement = new DeviceElement();

            //ID
            deviceElement.Id.UniqueIds.AddRange(ImportUniqueIDs(isoDeviceElement.DeviceElementId));
            TaskDataMapper.ADAPTIdMap.Add(isoDeviceElement.DeviceElementId, deviceElement.Id.ReferenceId);

            //Device ID
            deviceElement.DeviceModelId = TaskDataMapper.ADAPTIdMap[isoDeviceElement.Device.DeviceId].Value;

            //Description
            deviceElement.Description = isoDeviceElement.DeviceElementDesignator;

            //Classification
            deviceElement.DeviceClassification = deviceClassification;

            //Parent ID
            if (isoDeviceElement.Parent != null)
            {
                if (isoDeviceElement.Parent is ISODeviceElement)
                {
                    ISODeviceElement parentElement = isoDeviceElement.Parent as ISODeviceElement;
                    deviceElement.ParentDeviceId = TaskDataMapper.ADAPTIdMap[parentElement.DeviceElementId].Value;
                }
                else
                {
                    ISODevice parentDevice = isoDeviceElement.Parent as ISODevice;
                    deviceElement.ParentDeviceId = TaskDataMapper.ADAPTIdMap[parentDevice.DeviceId].Value;
                }
            }

            DeviceElementHierarchy deviceElementHierarchy = TaskDataMapper.DeviceElementHierarchies.GetRelevantHierarchy(isoDeviceElement.DeviceElementId);

            //Device Element Type
            switch (isoDeviceElement.DeviceElementType)
            {
                case ISODeviceElementType.Device:  //This is the root device element
                    if (deviceClassification != null &&
                        deviceClassification.Value != null &&
                        TaskDataMapper.DeviceOperationTypes.First(d => d.MachineEnumerationMember.DomainTag == deviceClassification.Value.Code).HasMachineConfiguration)
                    {
                        //Device is a machine
                        deviceElement.DeviceElementType = DeviceElementTypeEnum.Machine;
                    }
                    else if (deviceElementHierarchy.Children != null && deviceElementHierarchy.Children.Any(h => h.DeviceElement.DeviceElementType == ISODeviceElementType.Navigation))
                    {
                        //Device has a navigation element; classify as a machine
                        deviceElement.DeviceElementType = DeviceElementTypeEnum.Machine;
                    }
                    else
                    {
                        //Default: classify as an implement
                        deviceElement.DeviceElementType = DeviceElementTypeEnum.Implement;
                    }
                    break;
                case ISODeviceElementType.Bin:
                    deviceElement.DeviceElementType = DeviceElementTypeEnum.Bin;
                    break;
                case ISODeviceElementType.Function:
                    deviceElement.DeviceElementType = DeviceElementTypeEnum.Function;
                    break;
                case ISODeviceElementType.Section:
                    deviceElement.DeviceElementType = DeviceElementTypeEnum.Section;
                    break;
                case ISODeviceElementType.Unit:
                    deviceElement.DeviceElementType = DeviceElementTypeEnum.Unit;
                    break;
                case ISODeviceElementType.Navigation:
                    deviceElement.DeviceElementType = DeviceElementTypeEnum.Function;
                    break;
            }


            if (HasGeometryInformation(deviceElementHierarchy)) //Geometry information is on DeviceProperty elements. 
            {
                GetDeviceElementConfiguration(deviceElement, deviceElementHierarchy, DataModel.Catalog); //Add via the Get method to invoke business rules for configs
            }

            return deviceElement;
        }

        private bool HasGeometryInformation(DeviceElementHierarchy deviceElementHierarchy)
        {
            return deviceElementHierarchy.Width.HasValue || deviceElementHierarchy.XOffset.HasValue || deviceElementHierarchy.YOffset.HasValue || deviceElementHierarchy.ZOffset.HasValue;
        }

        public static DeviceElementConfiguration GetDeviceElementConfiguration(DeviceElement adaptDeviceElement, DeviceElementHierarchy isoHierarchy, AgGateway.ADAPT.ApplicationDataModel.ADM.Catalog catalog)
        {
            if ((isoHierarchy.DeviceElement.DeviceElementType == ISOEnumerations.ISODeviceElementType.Bin
                     && (isoHierarchy.Parent.DeviceElement.DeviceElementType == ISOEnumerations.ISODeviceElementType.Function ||
                         isoHierarchy.Parent.DeviceElement.DeviceElementType == ISOEnumerations.ISODeviceElementType.Device)) ||
                 (isoHierarchy.DeviceElement.DeviceElementType == ISOEnumerations.ISODeviceElementType.Connector) ||
                 (isoHierarchy.DeviceElement.DeviceElementType == ISOEnumerations.ISODeviceElementType.Navigation))
            {
                //Data belongs to the parent device element from the ISO element referenced

                //Bin children of functions or devices carry data that effectively belong to the parent device element in ISO.  See TC-GEO examples 5-8.
                //Find the parent DeviceElementUse and add the data to that object.
                //Per the TC-GEO spec: "The location of the Bin type device elements as children of the boom specifies that the products from these bins are all distributed through that boom."
                //-
                //Also, Connector and Navigation data may be stored in Timelog data, but Connectors are not DeviceElements in ADAPT.  The data refers to the parent implement.
                DeviceElementConfiguration parentConfig = catalog.DeviceElementConfigurations.FirstOrDefault(c => c.DeviceElementId == adaptDeviceElement.ParentDeviceId);
                if (parentConfig == null)
                {
                    DeviceElement parentElement = catalog.DeviceElements.Single(d => d.Id.ReferenceId == adaptDeviceElement.ParentDeviceId);
                    parentConfig = AddDeviceElementConfiguration(parentElement, isoHierarchy.Parent, catalog);
                }
                return parentConfig;
            }
            else
            {
                DeviceElementConfiguration deviceConfiguration = catalog.DeviceElementConfigurations.FirstOrDefault(c => c.DeviceElementId == adaptDeviceElement.Id.ReferenceId);
                if (deviceConfiguration == null)
                {
                    deviceConfiguration = AddDeviceElementConfiguration(adaptDeviceElement, isoHierarchy, catalog);
                }
                return deviceConfiguration;
            }
        }

        private static DeviceElementConfiguration AddDeviceElementConfiguration(DeviceElement adaptDeviceElement, DeviceElementHierarchy isoHierarchy, AgGateway.ADAPT.ApplicationDataModel.ADM.Catalog catalog)
        {
            switch (adaptDeviceElement.DeviceElementType)
            {
                case DeviceElementTypeEnum.Machine:
                    return AddMachineConfiguration(adaptDeviceElement, isoHierarchy, catalog);
                case DeviceElementTypeEnum.Implement:
                    return AddImplementConfiguration(adaptDeviceElement, isoHierarchy, catalog);
                case DeviceElementTypeEnum.Function:
                    if (isoHierarchy.Parent.DeviceElement.DeviceElementType == ISODeviceElementType.Function)
                    {
                        //Function is part of the implement.  I.e., TC-GEO example 9 / ISO 11783-10:2015(E) Figure F.24
                        return AddSectionConfiguration(adaptDeviceElement, isoHierarchy, catalog);
                    }
                    else
                    {                       
                        //Function is the entire implement
                        return AddImplementConfiguration(adaptDeviceElement, isoHierarchy, catalog);
                    }
                case DeviceElementTypeEnum.Section:
                case DeviceElementTypeEnum.Unit:
                    return AddSectionConfiguration(adaptDeviceElement, isoHierarchy, catalog);
                default:
                    return null;
            }
        }

        public static MachineConfiguration AddMachineConfiguration(DeviceElement adaptDeviceElement, DeviceElementHierarchy deviceHierarchy, AgGateway.ADAPT.ApplicationDataModel.ADM.Catalog catalog)
        {
            MachineConfiguration machineConfig = new MachineConfiguration();

            //Description
            machineConfig.Description = deviceHierarchy.DeviceElement.DeviceElementDesignator;

            //Device Element ID
            machineConfig.DeviceElementId = adaptDeviceElement.Id.ReferenceId;

            //Offsets
            if (deviceHierarchy.XOffset.HasValue ||
                deviceHierarchy.YOffset.HasValue ||
                deviceHierarchy.ZOffset.HasValue)
            {
                machineConfig.Offsets = new List<NumericRepresentationValue>();
                if (deviceHierarchy.XOffset != null)
                {
                    machineConfig.Offsets.Add(deviceHierarchy.XOffsetRepresentation);
                }
                if (deviceHierarchy.YOffset != null)
                {
                    machineConfig.Offsets.Add(deviceHierarchy.YOffsetRepresentation);
                }
                if (deviceHierarchy.ZOffset != null)
                {
                    machineConfig.Offsets.Add(deviceHierarchy.ZOffsetRepresentation);
                }
            }

            //GPS Offsets
            if (deviceHierarchy.Children != null && deviceHierarchy.Children.Any(h => h.DeviceElement.DeviceElementType == ISODeviceElementType.Navigation))
            {
                DeviceElementHierarchy navigation = (deviceHierarchy.Children.First(h => h.DeviceElement.DeviceElementType == ISODeviceElementType.Navigation));
                machineConfig.GpsReceiverXOffset = navigation.XOffsetRepresentation;
                machineConfig.GpsReceiverYOffset = navigation.YOffsetRepresentation;
                machineConfig.GpsReceiverZOffset = navigation.ZOffsetRepresentation;
            }
            

            catalog.DeviceElementConfigurations.Add(machineConfig);
            return machineConfig;
        }

        public static ImplementConfiguration AddImplementConfiguration(DeviceElement adaptDeviceElement, DeviceElementHierarchy deviceHierarchy, AgGateway.ADAPT.ApplicationDataModel.ADM.Catalog catalog)
        {
            ImplementConfiguration implementConfig = new ImplementConfiguration();

            //Description
            implementConfig.Description = deviceHierarchy.DeviceElement.DeviceElementDesignator;

            //Device Element ID
            implementConfig.DeviceElementId = adaptDeviceElement.Id.ReferenceId;

            //Offsets
            implementConfig.Offsets = new List<NumericRepresentationValue>();
            if (deviceHierarchy.XOffsetRepresentation != null)
            {
                implementConfig.Offsets.Add(deviceHierarchy.XOffsetRepresentation);
            }
            if (deviceHierarchy.YOffsetRepresentation != null)
            {
                implementConfig.Offsets.Add(deviceHierarchy.YOffsetRepresentation);
            }
            if (deviceHierarchy.ZOffsetRepresentation != null)
            {
                implementConfig.Offsets.Add(deviceHierarchy.ZOffsetRepresentation);
            }

            //Total Width 
            if (deviceHierarchy.Width != null)
            {
                implementConfig.PhysicalWidth = deviceHierarchy.WidthRepresentation;
            }

            //Row Width
            NumericRepresentationValue rowWidth = deviceHierarchy.GetLowestLevelSectionWidth();
            if (rowWidth != null)
            {
                implementConfig.Width = rowWidth;
            }
            
            catalog.DeviceElementConfigurations.Add(implementConfig);

            return implementConfig;
        }

        public static SectionConfiguration AddSectionConfiguration(DeviceElement adaptDeviceElement, DeviceElementHierarchy deviceHierarchy, AgGateway.ADAPT.ApplicationDataModel.ADM.Catalog catalog)
        {
            SectionConfiguration sectionConfiguration = new SectionConfiguration();

            //Description
            sectionConfiguration.Description = deviceHierarchy.DeviceElement.DeviceElementDesignator;

            //Device Element ID
            sectionConfiguration.DeviceElementId = adaptDeviceElement.Id.ReferenceId;

            //Width & Offsets
            if (deviceHierarchy.Width != null)
            {
                sectionConfiguration.SectionWidth = deviceHierarchy.WidthRepresentation;
            }

            sectionConfiguration.Offsets = new List<NumericRepresentationValue>();
            if (deviceHierarchy.XOffset != null)
            {
                sectionConfiguration.InlineOffset = deviceHierarchy.XOffsetRepresentation;
                sectionConfiguration.Offsets.Add(deviceHierarchy.XOffsetRepresentation);
            }
            if (deviceHierarchy.YOffset != null)
            {
                sectionConfiguration.LateralOffset = deviceHierarchy.YOffsetRepresentation;
                sectionConfiguration.Offsets.Add(deviceHierarchy.YOffsetRepresentation);
            }

            catalog.DeviceElementConfigurations.Add(sectionConfiguration);
            return sectionConfiguration;
        }

        /// <summary>
        /// Adds a connector with a hitch at the given reference point, referencing the parent DeviceElement as the DeviceElementConfiguration
        /// </summary>
        /// <param name="rootDeviceHierarchy"></param>
        /// <param name="connectorDeviceElement"></param>
        /// <param name="deviceElement"></param>
        private void AddConnector(DeviceElementHierarchy rootDeviceHierarchy, ISODeviceElement connectorDeviceElement)
        {
            //Per the TC-GEO specification, the connector is always a child of the root device element.
            DeviceElementHierarchy hierarchy = rootDeviceHierarchy.FromDeviceElementID(connectorDeviceElement.DeviceElementId);
            if (hierarchy.Parent == rootDeviceHierarchy)
            {
                int? rootDeviceElementID = TaskDataMapper.ADAPTIdMap.FindByISOId(rootDeviceHierarchy.DeviceElement.DeviceElementId);
                if (rootDeviceElementID.HasValue)
                {
                    HitchPoint hitch = new HitchPoint();
                    hitch.ReferencePoint = new ReferencePoint() { XOffset = hierarchy.XOffsetRepresentation, YOffset = hierarchy.YOffsetRepresentation, ZOffset = hierarchy.ZOffsetRepresentation };
                    hitch.HitchTypeEnum = HitchTypeEnum.Unkown;
                    DataModel.Catalog.HitchPoints.Add(hitch);

                    //Get the DeviceElementConfiguration for the root element in order that we may link the Connector to it.
                    DeviceElement root = DataModel.Catalog.DeviceElements.FirstOrDefault(d => d.Id.ReferenceId == rootDeviceElementID);
                    if (root != null)
                    {
                        DeviceElementConfiguration rootDeviceConfiguration = DeviceElementMapper.GetDeviceElementConfiguration(root, rootDeviceHierarchy, DataModel.Catalog);
                        if (rootDeviceConfiguration != null)
                        {
                            Connector connector = new Connector();
                            connector.DeviceElementConfigurationId = rootDeviceConfiguration.Id.ReferenceId;
                            connector.HitchPointId = hitch.Id.ReferenceId;
                            DataModel.Catalog.Connectors.Add(connector);

                            TaskDataMapper.ADAPTIdMap.Add(connectorDeviceElement.DeviceElementId, connector.Id.ReferenceId);
                        }
                    }
                }
            }
        }

        private EnumeratedValue DecodeMachineInfo(string clientNAME)
        {
            if (string.IsNullOrEmpty(clientNAME) ||
                clientNAME.Length != 16)
                return null;

            byte deviceGroup;
            if (!byte.TryParse(clientNAME.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out deviceGroup))
                return null;
            deviceGroup >>= 4;

            if ((deviceGroup & 0x07) != 2) // Agricultural devices
                return null;

            byte deviceClass;
            if (!byte.TryParse(clientNAME.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out deviceClass))
                return null;
            deviceClass >>= 1;

            AgGateway.ADAPT.ApplicationDataModel.Representations.EnumerationMember machineType = DefinedTypeEnumerationInstanceList.dtiTractor.ToModelEnumMember(); //Default

            DeviceOperationType deviceType = DeviceOperationTypes.SingleOrDefault(d => d.ClientNAMEMachineType == deviceClass);
            if (deviceType != null)
            {
                machineType = deviceType.MachineEnumerationMember.ToModelEnumMember();
            }

            return new EnumeratedValue { Representation = RepresentationInstanceList.dtMachineType.ToModelRepresentation(), Value = machineType };
        }

        #endregion Import
    }
}
