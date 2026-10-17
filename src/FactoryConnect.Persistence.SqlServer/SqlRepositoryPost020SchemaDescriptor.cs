namespace FactoryConnect.Persistence.SqlServer;

internal static class SqlRepositoryPost020SchemaDescriptor
{
    public static SqlSchemaDescriptor Create(SqlSchemaDescriptor previous) => new([.. previous.Tables,
        new SqlTableDescriptor(new("dbo", "AcquisitionEvidencePublication"),
            [C("MachineId","uniqueidentifier"), C("StreamKeyBinary","varbinary",SqlLengthDescriptor.Bounded(512)),
             C("IdentityHash","binary",SqlLengthDescriptor.Bounded(32)), C("PublicationOrdinal","bigint"),
             C("PreviousIdentity","binary",SqlLengthDescriptor.Bounded(32),true),
             new("AcceptedBefore","decimal",null,20,0,true,null,null), new("AcceptedThrough","decimal",null,20,0,true,null,null),
             C("PayloadVersion","int"), C("ContentHash","binary",SqlLengthDescriptor.Bounded(32)), C("PayloadBinary","varbinary",SqlLengthDescriptor.Max)],
            new("PK_AcquisitionEvidencePublication",new(true,[K("MachineId",1),K("StreamKeyBinary",2),K("IdentityHash",3)],[],null)), [],
            [new("FK_AcquisitionEvidencePublication_Stream",["MachineId","StreamKeyBinary"],new("dbo","ObservationStreamCheckpoint"),
                ["MachineId","StreamKeyBinary"],SqlReferentialAction.NoAction,SqlReferentialAction.NoAction,true,true,false),
             new("FK_AcquisitionEvidencePublication_Previous",["MachineId","StreamKeyBinary","PreviousIdentity"],new("dbo","AcquisitionEvidencePublication"),
                ["MachineId","StreamKeyBinary","IdentityHash"],SqlReferentialAction.NoAction,SqlReferentialAction.NoAction,true,true,false)],
            [Check("Ordinal","([PublicationOrdinal]>(0))"),Check("Version","([PayloadVersion]>(0))"),
                Check("Size","(datalength([PayloadBinary])>=(1) AND datalength([PayloadBinary])<=(16777216))")],
            [new("UX_AcquisitionEvidencePublication_Ordinal",true,true,new(false,[K("MachineId",1),K("StreamKeyBinary",2),K("PublicationOrdinal",3)],[],null))]),
        new SqlTableDescriptor(new("dbo","AcquisitionEvidenceHead"),
            [C("MachineId","uniqueidentifier"),C("StreamKeyBinary","varbinary",SqlLengthDescriptor.Bounded(512)),C("IdentityHash","binary",SqlLengthDescriptor.Bounded(32))],
            new("PK_AcquisitionEvidenceHead",new(true,[K("MachineId",1),K("StreamKeyBinary",2)],[],null)),[],
            [new("FK_AcquisitionEvidenceHead_Publication",["MachineId","StreamKeyBinary","IdentityHash"],new("dbo","AcquisitionEvidencePublication"),
                ["MachineId","StreamKeyBinary","IdentityHash"],SqlReferentialAction.NoAction,SqlReferentialAction.NoAction,true,true,false)],[],[])]);
    private static SqlColumnDescriptor C(string n,string t,SqlLengthDescriptor? l=null,bool nullable=false) => new(n,t,l,null,null,nullable,null,null);
    private static SqlIndexColumnDescriptor K(string n,int ordinal) => new(n,SqlIndexColumnDirection.Ascending,ordinal);
    private static SqlCheckConstraintDescriptor Check(string suffix,string definition) => new("CK_AcquisitionEvidencePublication_"+suffix,definition,true,true,false);
}
